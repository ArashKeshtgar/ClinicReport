using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ClinicReport.Api.Auth;
using Microsoft.Extensions.Options;

namespace ClinicReport.Tests;

public class MailTests
{
    [Fact]
    public async Task A_server_that_never_answers_fails_the_send_instead_of_hanging()
    {
        // Accepts the connection and never sends the SMTP greeting (on Windows, port 1025 is often such a service).
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sender = new SmtpMailSender(Options.Create(new MailOptions { SmtpHost = "127.0.0.1", SmtpPort = port, TimeoutSeconds = 1 }));

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(new OutgoingMail("owner@example.com", "Subject", "Body")));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        (await accepted).Dispose();
    }

    [Fact]
    public async Task Without_a_server_set_nothing_is_sent()
    {
        var sender = new SmtpMailSender(Options.Create(new MailOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new OutgoingMail("owner@example.com", "Subject", "Body")));
    }
}
