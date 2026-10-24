# Gamex — Notification Service Specification

> **Repository:** `gamex-rs/gamex-notification-svc`  
> **Version:** 1.0  
> **Date:** 2026-05-12

---

## 1. Purpose

The Notification Service creates and delivers in-app notifications to users. It consumes events from SQS (published by User Library Service and Messaging Service), persists notifications, and pushes real-time updates to connected clients via SignalR. It does not call other services at request time — all data needed to construct a notification is carried in the incoming event payload.

---

## 2. Tech Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10 |
| Framework | ASP.NET Core Web API + SignalR |
| ORM | Entity Framework Core 8 |
| Database | MySQL 8 (Aurora MySQL Serverless v2 on AWS, official Docker image locally) |
| Real-time | SignalR (WebSocket with long-polling fallback) |
| SQS Consumer | AWSSDK.SQS + background `IHostedService` |
| Auth | JWT RS256 — validated using public key from SSM (issued by User Library Service) |
| Config / Secrets | AWS SSM Parameter Store + Secrets Manager (env vars locally) |

---

## 3. API Endpoints

Base path: `/api/v1`

All endpoints require a valid Bearer JWT. Users can only access their own notifications.

| Method | Path | Description |
|---|---|---|
| `GET` | `/notifications` | Paginated list of own notifications (newest first) |
| `GET` | `/notifications/unread-count` | Returns `{ "count": 5 }` — used for the bell badge |
| `PUT` | `/notifications/{id}/read` | Mark single notification as read |
| `PUT` | `/notifications/read-all` | Mark all unread notifications as read |

#### `GET /notifications` — Response

```json
{
  "items": [
    {
      "id": "uuid",
      "type": "new_message",
      "read": false,
      "createdAt": "2026-05-12T10:00:00Z",
      "data": {
        "conversationId": "uuid",
        "senderName": "Marko P.",
        "gameTitle": "The Last of Us Part II"
      }
    }
  ],
  "nextCursor": "string"
}
```

The `data` object shape varies by notification type (see Section 5).

---

## 4. SignalR Hub

### Hub URL

`/hubs/notifications`

### Authentication

JWT passed as query parameter `?access_token=<jwt>`.

### Client Groups

On connect, each authenticated user is added to group `user:{userId}`.

### Server → Client Events

| Event name | Payload | When sent |
|---|---|---|
| `NotificationReceived` | Full notification object | New notification created for user |
| `UnreadCountUpdated` | `{ "count": 5 }` | After any read/unread state change |

---

## 5. Data Model

### `notifications`

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) PK | UUID |
| `user_id` | CHAR(36) | Recipient; no FK across services |
| `type` | VARCHAR(100) | See notification types below |
| `payload` | JSON | Type-specific data for the frontend |
| `read_at` | DATETIME | Null = unread |
| `created_at` | DATETIME | |

Index on `(user_id, created_at DESC)` and `(user_id, read_at)`.

---

## 6. Notification Types & Payloads

| Type | Trigger event | Payload fields |
|---|---|---|
| `new_message` | `message.sent` | `conversationId`, `senderName`, `gameTitle` |
| `wishlist_match` | `listing.created` | `listingId`, `gameId`, `gameTitle`, `sellerName`, `listingType` |
| `listing_expiring` | `listing.expired` | `listingId`, `gameTitle`, `expiresAt` |
| `rate_your_partner` | `transaction.completed` | `listingId`, `gameTitle`, `partnerUserId`, `partnerName` |

---

## 7. SQS Event Consumer

### Queue: `gamex-activity-events`

The consumer is a background `IHostedService` that polls SQS in a loop.

### Handled Events

#### `message.sent`

Creates a `new_message` notification for the recipient.

```json
{
  "eventType": "message.sent",
  "conversationId": "uuid",
  "messageId": "uuid",
  "senderId": "uuid",
  "recipientId": "uuid",
  "occurredAt": "..."
}
```

Payload stored: `{ conversationId, senderName (resolved from event or future enrichment), gameTitle }`.

> **Note:** The event currently carries `senderId` and `recipientId` but not display names. For Phase 1, store the IDs in the payload and let the frontend resolve names via the User Library Service. In Phase 2, enrich events at publish time with display names to avoid cross-service calls.

#### `listing.created`

Finds users whose wishlist contains the game (this service does **not** query the library database — the event must carry enough data or wishlist matching moves to User Library Service which publishes per-user notifications directly). 

**Phase 1 approach:** User Library Service is responsible for resolving wishlist matches at publish time and emitting one `listing.created` event per matching user. A user matches only when their wishlist entry is for the same game **and the same platform** as the listing (format does not need to match). The Notification Service simply creates one notification per event received.

```json
{
  "eventType": "listing.created",
  "listingId": "uuid",
  "gameId": "uuid",
  "gameTitle": "string",
  "userId": "uuid",
  "sellerName": "string",
  "listingType": "exchange",
  "notifyUserId": "uuid",
  "occurredAt": "..."
}
```

#### `listing.expired`

Creates a `listing_expiring` notification for the listing owner.

```json
{
  "eventType": "listing.expired",
  "listingId": "uuid",
  "gameTitle": "string",
  "userId": "uuid",
  "occurredAt": "..."
}
```

#### `transaction.completed`

Creates a `rate_your_partner` notification for both parties.

```json
{
  "eventType": "transaction.completed",
  "listingEntryId": "uuid",
  "gameTitle": "string",
  "userIdA": "uuid",
  "userNameA": "string",
  "userIdB": "uuid",
  "userNameB": "string",
  "occurredAt": "..."
}
```

### Consumer Behaviour

1. Poll SQS with long-polling (20s wait)
2. For each message: deserialize, create notification row, push via SignalR, delete SQS message
3. On failure: do not delete message (SQS redelivery), allow up to 3 retries, then message moves to DLQ (`gamex-activity-events-dlq`)

---

## 8. Error Handling

All REST error responses follow RFC 7807 Problem Details.

---

## 9. Local Development

```yaml
# docker-compose.yml
services:
  notification-svc:
    build: .
    ports:
      - "5004:8080"
    environment:
      - DB_CONNECTION_STRING=Server=notification-db;Database=notifications_db;User=root;Password=localdev;
      - JWT_PUBLIC_KEY=${JWT_PUBLIC_KEY}
      - SQS_ACTIVITY_EVENTS_URL=http://localstack:4566/000000000000/gamex-activity-events
    depends_on:
      - notification-db

  notification-db:
    image: mysql:8.0
    environment:
      MYSQL_DATABASE: notifications_db
      MYSQL_ROOT_PASSWORD: localdev
    ports:
      - "3310:3306"
    volumes:
      - notification-db-data:/var/lib/mysql

volumes:
  notification-db-data:
```

---

## 10. CI/CD

| Workflow | Trigger | Action |
|---|---|---|
| `ci.yml` | Push / PR | Build + unit + integration tests |
| `deploy-dev.yml` | Merge to `main` | Build image → ECR → deploy to `gx-dev` ECS |
| `deploy-prod.yml` | Tag `v*.*.*` | Manual approval → ECR → deploy to `gx-prod` ECS |

---

## 11. Environment Variables Reference

| Variable | Description | Source (AWS) |
|---|---|---|
| `DB_CONNECTION_STRING` | MySQL connection string | Secrets Manager |
| `JWT_PUBLIC_KEY` | RS256 public key (PEM) | SSM Parameter Store |
| `SQS_ACTIVITY_EVENTS_URL` | Queue URL to consume | SSM Parameter Store |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | ECS task definition |

---

## 12. Implementation Notes (2026-10-24)

Decisions taken while implementing; they refine the sections above.

### 12.1 Payloads (the `data` object)

Producers now enrich their events, so no name lookups are needed (this replaces the Phase 1 note in §7):

| Type | `data` |
|---|---|
| `new_message` | `conversationId, senderId, senderName, gameTitle, listingId` |
| `wishlist_match` | `listingId, gameId, gameTitle, sellerName, listingType (exchange/sale), platformSlug, format` |
| `listing_expiring` | `listingId, gameTitle, expiresAt` |
| `rate_your_partner` | `listingId, transactionId, gameTitle, partnerUserId, partnerName` (one per party; `transactionId` is what `POST /users/{id}/ratings` needs) |

### 12.2 Consumer

- All notifications of one SQS message are inserted in one transaction. A unique key on `(source_message_id, user_id)` makes redelivery idempotent: a duplicate is acknowledged without new notifications.
- After commit: SignalR `NotificationReceived` (the notification as returned by the API) and `UnreadCountUpdated { count }` to `user:{userId}`.
- Malformed events (invalid JSON, missing `eventType` or required ids) stay on the queue → DLQ after 3 receives. Events of an **unknown** `eventType` are logged and deleted, so new producers cannot clog the queue.
- A leading UTF-8 BOM in a message body is tolerated.

### 12.3 API details

- `GET /notifications?cursor&pageSize` (default 20, max 50), newest first; items `{ id, type, read, createdAt, data }`.
- `PUT /notifications/{id}/read` → `204`; another user's notification → `404`. `PUT /notifications/read-all` → `204`. Both push `UnreadCountUpdated` when something changed.

### 12.4 Data model additions

`notifications.source_message_id` VARCHAR(100) (SQS `MessageId`), unique `(source_message_id, user_id)`; `payload` is a MySQL `JSON` column.

### 12.5 Operations

- Several instances need a SignalR backplane (e.g. Redis) — not configured yet.
- Additional environment variables: `JWT_ISSUER` / `JWT_AUDIENCE` (validated when set), `AWS_SERVICE_URL` / `AWS_DEFAULT_REGION` (LocalStack), `DB_MIGRATE_ON_STARTUP`.
