using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Gamex.Notifications.Application.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.LocalStack;
using Testcontainers.MySql;

namespace Gamex.Notifications.IntegrationTests;

/// <summary>API + SQS consumer against MySQL and LocalStack. Requires Docker.</summary>
public sealed class NotificationsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048));
    private readonly MySqlContainer _mySql = new MySqlBuilder("mysql:8.0").WithDatabase("notifications_db").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:3").Build();

    public string QueueUrl { get; private set; } = "";
    public string DlqUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_mySql.StartAsync(), _localStack.StartAsync());
        using var sqs = CreateSqsClient();
        DlqUrl = (await sqs.CreateQueueAsync("gamex-activity-events-dlq")).QueueUrl;
        var dlqArn = (await sqs.GetQueueAttributesAsync(DlqUrl, ["QueueArn"])).QueueARN;
        QueueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "gamex-activity-events",
            Attributes = new Dictionary<string, string>
            {
                ["VisibilityTimeout"] = "1",
                ["RedrivePolicy"] = $$"""{"deadLetterTargetArn":"{{dlqArn}}","maxReceiveCount":"3"}""",
            },
        })).QueueUrl;
        _ = Services;
    }

    public AmazonSQSClient CreateSqsClient() => new(
        new BasicAWSCredentials("test", "test"),
        new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString(), AuthenticationRegion = "eu-central-1" });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DB_CONNECTION_STRING", _mySql.GetConnectionString());
        builder.UseSetting("DB_MIGRATE_ON_STARTUP", "true");
        builder.UseSetting("JWT_PUBLIC_KEY", SigningKey.Rsa.ExportSubjectPublicKeyInfoPem());
        builder.UseSetting("JWT_ISSUER", "gamex-user-library");
        builder.UseSetting("JWT_AUDIENCE", "gamex");
        builder.UseSetting("AWS_SERVICE_URL", _localStack.GetConnectionString());
        builder.UseSetting("SQS_ACTIVITY_EVENTS_URL", QueueUrl);
    }

    public (Guid Id, string Token, HttpClient Client) CreateUser()
    {
        var id = Guid.CreateVersion7();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "gamex-user-library",
            Audience = "gamex",
            Expires = DateTime.UtcNow.AddMinutes(15),
            Subject = new ClaimsIdentity([new Claim("sub", id.ToString()), new Claim("name", "Test"), new Claim("role", "user")]),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (id, token, client);
    }

    public async Task<HubConnection> ConnectHubAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _mySql.DisposeAsync();
        await _localStack.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class NotificationsApiFixture : ICollectionFixture<NotificationsApiFactory>
{
    public const string Name = "NotificationsApi";
}

[Collection(NotificationsApiFixture.Name)]
public sealed class NotificationTests(NotificationsApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SendAsync(string body)
    {
        using var sqs = factory.CreateSqsClient();
        await sqs.SendMessageAsync(factory.QueueUrl, body, Ct);
    }

    private static string MessageSent(Guid recipient, string title = "Hades") =>
        $$"""{"eventType":"message.sent","conversationId":"{{Guid.NewGuid()}}","messageId":"{{Guid.NewGuid()}}","senderId":"{{Guid.NewGuid()}}","senderName":"Marko P.","recipientId":"{{recipient}}","listingId":"{{Guid.NewGuid()}}","gameTitle":"{{title}}","occurredAt":"2026-10-24T10:00:00Z"}""";

    private static async Task<CursorPage<NotificationDto>> WaitForAsync(HttpClient client, int count, string query = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        CursorPage<NotificationDto>? page;
        do
        {
            page = await client.GetFromJsonAsync<CursorPage<NotificationDto>>($"/api/v1/notifications{query}", Ct);
            if (page!.Items.Count >= count)
            {
                return page;
            }

            await Task.Delay(200, Ct);
        }
        while (DateTime.UtcNow < deadline);

        return page;
    }

    [Fact]
    public async Task MessageSent_CreatesNewMessageNotification()
    {
        var user = factory.CreateUser();

        await SendAsync(MessageSent(user.Id, "The Last of Us Part II"));

        var notification = (await WaitForAsync(user.Client, 1)).Items.ShouldHaveSingleItem();
        notification.Type.ShouldBe("new_message");
        notification.Read.ShouldBeFalse();
        notification.Data.GetProperty("senderName").GetString().ShouldBe("Marko P.");
        notification.Data.GetProperty("gameTitle").GetString().ShouldBe("The Last of Us Part II");
        (await user.Client.GetFromJsonAsync<UnreadCountDto>("/api/v1/notifications/unread-count", Ct))!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TransactionCompleted_NotifiesBothParties()
    {
        var a = factory.CreateUser();
        var b = factory.CreateUser();

        await SendAsync($$"""{"eventType":"transaction.completed","transactionId":"{{Guid.NewGuid()}}","listingEntryId":"{{Guid.NewGuid()}}","gameTitle":"Gris","userIdA":"{{a.Id}}","userNameA":"Ana","userIdB":"{{b.Id}}","userNameB":"Boris","occurredAt":"2026-10-24T10:00:00Z"}""");

        (await WaitForAsync(a.Client, 1)).Items.ShouldHaveSingleItem().Data.GetProperty("partnerName").GetString().ShouldBe("Boris");
        (await WaitForAsync(b.Client, 1)).Items.ShouldHaveSingleItem().Data.GetProperty("partnerName").GetString().ShouldBe("Ana");
    }

    [Fact]
    public async Task SameSqsMessageTwice_CreatesOneNotification()
    {
        var user = factory.CreateUser();
        var requests = new[] { new NotificationRequest(user.Id, "listing_expiring", new { listingId = Guid.NewGuid(), gameTitle = "Tunic" }) };

        await using var scope = factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IActivityEventHandler>();
        (await handler.HandleAsync("sqs-msg-1", requests, Ct)).ShouldBe(HandleOutcome.Created);
        (await handler.HandleAsync("sqs-msg-1", requests, Ct)).ShouldBe(HandleOutcome.Duplicate);

        (await user.Client.GetFromJsonAsync<CursorPage<NotificationDto>>("/api/v1/notifications", Ct))!.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAndReadAll_UpdateUnreadCount_OthersCannotRead()
    {
        var user = factory.CreateUser();
        var other = factory.CreateUser();
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(MessageSent(user.Id, $"Game {i}"));
        }

        var page = await WaitForAsync(user.Client, 3);
        var first = page.Items[0];

        (await other.Client.PutAsync(new Uri($"/api/v1/notifications/{first.Id}/read", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await user.Client.PutAsync(new Uri($"/api/v1/notifications/{first.Id}/read", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await user.Client.GetFromJsonAsync<UnreadCountDto>("/api/v1/notifications/unread-count", Ct))!.Count.ShouldBe(2);

        (await user.Client.PutAsync(new Uri("/api/v1/notifications/read-all", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await user.Client.GetFromJsonAsync<UnreadCountDto>("/api/v1/notifications/unread-count", Ct))!.Count.ShouldBe(0);
        (await user.Client.GetFromJsonAsync<CursorPage<NotificationDto>>("/api/v1/notifications", Ct))!.Items.ShouldAllBe(n => n.Read);
    }

    [Fact]
    public async Task List_PagesNewestFirst()
    {
        var user = factory.CreateUser();
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(MessageSent(user.Id, $"Game {i}"));
            await WaitForAsync(user.Client, i + 1);
        }

        var first = await user.Client.GetFromJsonAsync<CursorPage<NotificationDto>>("/api/v1/notifications?pageSize=2", Ct);
        first!.Items.Select(n => n.Data.GetProperty("gameTitle").GetString()).ShouldBe(["Game 2", "Game 1"]);
        var second = await user.Client.GetFromJsonAsync<CursorPage<NotificationDto>>($"/api/v1/notifications?pageSize=2&cursor={first.NextCursor}", Ct);
        second!.Items.ShouldHaveSingleItem().Data.GetProperty("gameTitle").GetString().ShouldBe("Game 0");
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task SignalR_PushesNotificationAndUnreadCount()
    {
        var user = factory.CreateUser();
        await using var hub = await factory.ConnectHubAsync(user.Token);
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = new TaskCompletionSource<UnreadCountDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<NotificationDto>("NotificationReceived", n => received.TrySetResult(n));
        hub.On<UnreadCountDto>("UnreadCountUpdated", c => count.TrySetResult(c));

        await SendAsync(MessageSent(user.Id, "Celeste"));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Data.GetProperty("gameTitle").GetString().ShouldBe("Celeste");
        (await count.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task MalformedEvent_EndsInDlq()
    {
        var marker = Guid.NewGuid().ToString();
        await SendAsync($$"""{"eventType":"message.sent","marker":"{{marker}}"}""");

        using var sqs = factory.CreateSqsClient();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = factory.DlqUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 }, Ct);
            if (response.Messages?.Any(m => m.Body.Contains(marker, StringComparison.Ordinal)) == true)
            {
                return;
            }
        }

        throw new TimeoutException("Malformed event did not reach the DLQ.");
    }

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        (await factory.CreateClient().GetAsync(new Uri("/api/v1/notifications", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
