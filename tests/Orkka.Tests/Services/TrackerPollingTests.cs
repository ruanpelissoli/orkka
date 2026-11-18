namespace Orkka.Tests.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;

public sealed class TicketNormalizerTests
{
    private static RawTicket Ticket(
        string title = "Checkout fails",
        string? description = "<p>Steps</p>",
        string[]? labels = null,
        RawAttachment[]? attachments = null,
        string? status = "To Do",
        DateTimeOffset? updatedAt = null)
        => new("SHOP-1", "10001", "https://acme.atlassian.net/browse/SHOP-1", "SHOP-100", title, "Bug", status, "todo", "High",
            description, "<ul><li>It works</li></ul>", null, labels ?? ["web", "checkout"], attachments ?? [],
            updatedAt ?? new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    private static TrackerTicket Normalize(RawTicket raw, string? knownHash = null, IReadOnlyList<RawComment>? comments = null)
    {
        var names = TrackerAttachmentCache.PlanNames(raw.Attachments);
        var metas = names.Select((n, i) => new TrackerAttachmentMeta(n.FileName, raw.Attachments[i].Size, raw.Attachments[i].MimeType, true)).ToArray();
        return TicketNormalizer.Normalize(raw, names, metas, knownHash, comments ?? []);
    }

    [Fact]
    public void InlineFormatting_LinksAndCode_BecomeMarkdown()
    {
        var md = TicketNormalizer.ToMarkdown(
            "<p>Hello <strong>world</strong>, <b>bold</b> and <em>you</em></p><p>Run <code>npm test</code> then see "
            + "<a href=\"https://example.com/docs\">the docs</a>.</p><pre><code>var x = 1;\nvar y = 2;</code></pre>");

        md.Should().Contain("**world**").And.Contain("**bold**").And.Contain("*you*")
            .And.Contain("`npm test`").And.Contain("[the docs](https://example.com/docs)")
            .And.Contain("```").And.Contain("var x = 1;\nvar y = 2;");
        md.Should().NotContain("<");
    }

    [Fact]
    public void HeadingsAndNestedLists_KeepTheirStructure()
    {
        var md = TicketNormalizer.ToMarkdown(
            "<h2>Steps</h2><ul><li>one<ul><li>nested</li></ul></li><li>two</li></ul><ol><li>first</li><li>second</li></ol>");

        md.Should().Contain("## Steps").And.Contain("- one").And.Contain("- two").And.Contain("1. first").And.Contain("2. second");
        var nested = md.Split('\n').Single(l => l.Contains("nested", StringComparison.Ordinal));
        nested.Should().StartWith(" ", "a nested item is indented under its parent").And.Contain("- nested");
    }

    [Fact]
    public void Tables_BecomeRows()
    {
        var md = TicketNormalizer.ToMarkdown(
            "<table><tr><th>Field</th><th>Value</th></tr><tr><td>Total</td><td>42</td></tr><tr><td>Tax</td><td>3</td></tr></table>");

        md.Should().Contain("| Field | Value |").And.Contain("| Total | 42 |").And.Contain("| Tax | 3 |");
    }

    [Fact]
    public void ScriptsStylesAndComments_AreStripped_AndWhitespaceCollapses()
    {
        var md = TicketNormalizer.ToMarkdown(
            "<style>p { color: red }</style><p>first</p><script>alert('x')</script><!-- note --><p></p><p>&nbsp;</p><br><br><br>"
            + "<p>second&nbsp;&nbsp;</p><noscript>enable js</noscript>");

        md.Should().Be("first\n\nsecond");
    }

    [Fact]
    public void InlineImages_PointingAtAttachments_BecomeAttachmentReferences()
    {
        var attachments = new RawAttachment[]
        {
            new("10001", "error_screen.png", 100, "image/png", "https://acme.atlassian.net/rest/api/3/attachment/content/10001"),
            new("10002", "flow.png", 100, "image/png", "https://acme.atlassian.net/rest/api/3/attachment/content/10002"),
            new("4f2a9c1e-0000-4000-8000-000000000003", "trace.png", 100, null,
                "https://dev.azure.com/acme/0b1c/_apis/wit/attachments/4f2a9c1e-0000-4000-8000-000000000003"),
        };
        var names = TrackerAttachmentCache.PlanNames(attachments);

        var md = TicketNormalizer.ToMarkdown(
            "<p>Look: <img src=\"/rest/api/3/attachment/content/10001\" alt=\"x\"></p>"
            + "<p><span class=\"image-wrap\"><img src=\"https://acme.atlassian.net/secure/attachment/10002/10002_flow.png\"></span></p>"
            + "<p><img src=\"https://dev.azure.com/acme/0b1c/_apis/wit/attachments/4f2a9c1e-0000-4000-8000-000000000003?fileName=trace.png\"></p>"
            + "<p><img src=\"https://cdn.example.com/diagram.svg\" alt=\"diagram\"></p>"
            + "<p><img src=\"/images/icons/emoticons/smile.png\" alt=\"(smile)\"> thanks</p>"
            + "<p><img src=\"data:image/png;base64,AAAA\"></p>",
            names);

        md.Should().Contain("[attachment: error_screen.png]", "the file name is not markdown-escaped")
            .And.Contain("[attachment: flow.png]")
            .And.Contain("[attachment: trace.png]")
            .And.Contain("![diagram](https://cdn.example.com/diagram.svg)")
            .And.Contain("(smile) thanks");
        md.Should().NotContain("base64").And.NotContain("/rest/api/3").And.NotContain("emoticons");
    }

    [Fact]
    public void Description_AndComments_AreCapped()
    {
        var longText = "<p>" + new string('a', 30_000) + "</p>";
        var raw = Ticket(description: longText);

        var ticket = Normalize(raw, comments: [new RawComment("1", "Dana", DateTimeOffset.UtcNow, "<p>" + new string('b', 9_000) + "</p>")]);

        ticket.Body!.Description.Length.Should().BeLessThanOrEqualTo(TicketNormalizer.MaxDescriptionChars + TicketNormalizer.TruncatedSuffix.Length + 1);
        ticket.Body.Description.Should().EndWith(TicketNormalizer.TruncatedSuffix);
        ticket.NewComments.Single().Body.Length.Should().BeLessThanOrEqualTo(TicketNormalizer.MaxCommentChars + TicketNormalizer.TruncatedSuffix.Length + 1);
        ticket.NewComments.Single().Body.Should().EndWith(TicketNormalizer.TruncatedSuffix);
        TicketNormalizer.Cap("short", 10).Should().Be("short");
    }

    [Fact]
    public void Hash_IsStable_AndBodyIsNullWhenTheHostHasIt()
    {
        var first = Normalize(Ticket());
        var again = Normalize(Ticket());

        again.ContentHash.Should().Be(first.ContentHash).And.MatchRegex("^[0-9a-f]{64}$");
        first.Body.Should().NotBeNull();
        first.Body!.AcceptanceCriteria.Should().Contain("It works");

        var known = Normalize(Ticket(), knownHash: first.ContentHash);
        known.Body.Should().BeNull("the host already stored this content");
        known.Key.Should().Be("SHOP-1");
        known.EpicKey.Should().Be("SHOP-100");
        known.ContentHash.Should().Be(first.ContentHash);
    }

    [Fact]
    public void Hash_IgnoresCommentsUpdatedAtAndLabelOrder()
    {
        var baseline = Normalize(Ticket()).ContentHash;

        Normalize(Ticket(updatedAt: DateTimeOffset.UtcNow.AddDays(3))).ContentHash.Should().Be(baseline);
        Normalize(Ticket(), comments: [new RawComment("9", "Ana", DateTimeOffset.UtcNow, "<p>hi</p>")]).ContentHash.Should().Be(baseline);
        Normalize(Ticket(labels: ["checkout", "web"])).ContentHash.Should().Be(baseline);
        Normalize(Ticket(description: "<p>Steps</p>\n\n")).ContentHash.Should().Be(baseline, "whitespace is normalized first");

        // A status move — the developer's, or Orkka's own write-back — is not an edit of the
        // ticket: the body stays home and the host posts no "was updated" note.
        var moved = Normalize(Ticket(status: "In Progress"), knownHash: baseline);
        moved.ContentHash.Should().Be(baseline);
        moved.Body.Should().BeNull();
        moved.Status.Should().Be("In Progress", "the status itself still travels on every report");
    }

    [Fact]
    public void Hash_ChangesWithWhatTheTicketAsks()
    {
        var baseline = Normalize(Ticket()).ContentHash;

        Normalize(Ticket(title: "Checkout fails on Safari")).ContentHash.Should().NotBe(baseline);
        Normalize(Ticket(description: "<p>Other steps</p>")).ContentHash.Should().NotBe(baseline);
        Normalize(Ticket(labels: ["web"])).ContentHash.Should().NotBe(baseline);
        Normalize(Ticket(attachments: [new RawAttachment("1", "a.png", 5, null, "https://acme.atlassian.net/a")])).ContentHash.Should().NotBe(baseline);
    }
}

public sealed class JiraTicketFetchTests
{
    private const string Token = "ATATT3xFfGF0-secret-jira-token";
    private static readonly TrackerCredential Credential = new("https://acme.atlassian.net", "dana@acme.com", Token);
    private const string Myself = """{"accountId":"acc-dana","displayName":"Dana"}""";

    private static TrackerPollAssignment Assignment(bool descendants = false, string? extra = null,
        string scopeMode = TrackerScopeModes.Epics, string[]? intake = null, params string[] known)
        => new("cfg-1", "jira-acme", TrackerKinds.Jira, "SHOP", ["SHOP-100"], extra, descendants,
            [.. known.Select(k => new TrackerKnownTicket(k, null, null))], scopeMode, intake);

    private static string Issue(string key, string parent, string summary = "A ticket", string assignee = "acc-dana",
        string parentType = "Epic", int parentLevel = 1, string type = "Story", int level = 0, string subtasks = "[]",
        string status = "To Do", string category = "new")
        => $$"""
           {"id":"id-{{key}}","key":"{{key}}",
            "fields":{"summary":"{{summary}}","issuetype":{"name":"{{type}}","hierarchyLevel":{{level}}},
              "status":{"name":"{{status}}","statusCategory":{"key":"{{category}}"} },
              "priority":{"name":"High"},"labels":["web"],"updated":"2026-10-01T09:30:00.000+0000",
              "parent":{"key":"{{parent}}","fields":{"issuetype":{"name":"{{parentType}}","hierarchyLevel":{{parentLevel}} } } },
              "assignee":{"accountId":"{{assignee}}"},
              "components":[{"name":"Payments"}],
              "subtasks":{{subtasks}},
              "attachment":[{"id":"10001","filename":"shot.png","size":1234,"mimeType":"image/png",
                             "content":"https://acme.atlassian.net/rest/api/3/attachment/content/10001"}] },
            "renderedFields":{"description":"<p>Do <strong>it</strong></p>","customfield_10050":"<ul><li>works</li></ul>"} }
           """;

    private const string Fields = """
        [{"id":"summary","name":"Summary"},{"id":"customfield_10050","name":"Acceptance criteria","custom":true}]
        """;

    private static (HttpStatusCode, string, string) Json(string body) => (HttpStatusCode.OK, body, "application/json");

    private static bool IsTracking(string body) => FakeTrackerHandler.JqlOf(body).StartsWith("key in", StringComparison.Ordinal);

    [Fact]
    public async Task Intake_PagesThroughNextPageToken_WithRenderedFields_AndTheAcceptanceCriteriaField()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", Fields)
            .On(HttpMethod.Post, "/rest/api/3/search/jql", body =>
            {
                var doc = JsonDocument.Parse(body).RootElement;
                return doc.TryGetProperty("nextPageToken", out _)
                    ? Json($$"""{"issues":[{{Issue("SHOP-3", "SHOP-100")}}],"isLast":true}""")
                    : Json($$"""{"issues":[{{Issue("SHOP-1", "SHOP-100")}},{{Issue("SHOP-2", "SHOP-100")}}],"nextPageToken":"p2"}""");
            });
        var client = new JiraCloudClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential, Assignment(extra: "labels = web"), CancellationToken.None);

        tickets.Select(t => t.Key).Should().Equal("SHOP-1", "SHOP-2", "SHOP-3");
        tickets.Should().OnlyContain(t => t.InIntake && t.AssignedToMe);
        var searches = handler.Requests.Where(r => r.Url.Contains("/search/jql", StringComparison.Ordinal)).ToList();
        searches.Should().HaveCount(2, "nothing is tracked yet, so there is no tracking read");
        var firstBody = JsonDocument.Parse(searches[0].Body!).RootElement;
        firstBody.GetProperty("expand").GetString().Should().Be("renderedFields");
        firstBody.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should()
            .Contain(["summary", "description", "attachment", "updated", "parent", "subtasks", "assignee", "components", "customfield_10050"]);
        FakeTrackerHandler.JqlOf(searches[0].Body!).Should().Be(
            "project = \"SHOP\" AND assignee = currentUser() AND statusCategory = \"To Do\" AND parent in (\"SHOP-100\") AND (labels = web) ORDER BY key ASC");
        JsonDocument.Parse(searches[1].Body!).RootElement.GetProperty("nextPageToken").GetString().Should().Be("p2");

        var t = tickets[0];
        t.Url.Should().Be("https://acme.atlassian.net/browse/SHOP-1");
        t.EpicKey.Should().Be("SHOP-100");
        t.ParentKey.Should().Be("SHOP-100");
        t.ParentType.Should().Be("Epic");
        t.Components.Should().Equal("Payments");
        t.Type.Should().Be("Story");
        t.Status.Should().Be("To Do");
        t.StatusCategory.Should().Be("todo");
        t.Priority.Should().Be("High");
        t.DescriptionHtml.Should().Contain("<strong>it</strong>");
        t.AcceptanceCriteriaHtml.Should().Contain("works");
        t.UpdatedAt.Should().Be(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero));
        t.Attachments.Should().ContainSingle().Which.Should().Be(new RawAttachment("10001", "shot.png", 1234, "image/png",
            "https://acme.atlassian.net/rest/api/3/attachment/content/10001"));
    }

    [Fact]
    public async Task Intake_AllMyTickets_UsesTheConfiguredStatuses_AndNoEpic_AndSkipsEpicsThemselves()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", _ => Json($$"""
                {"issues":[{{Issue("SHOP-1", "SHOP-100")}},{{Issue("SHOP-5", "SHOP-0", type: "Epic", level: 1)}}],"isLast":true}
                """));
        var client = new JiraCloudClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential,
            Assignment(scopeMode: TrackerScopeModes.All, intake: ["To Do", "Selected for Development"]), CancellationToken.None);

        tickets.Select(t => t.Key).Should().Equal(["SHOP-1"], "an epic is a container, never a task");
        tickets[0].EpicKey.Should().Be("SHOP-100", "its parent is an epic");
        FakeTrackerHandler.JqlOf(handler.Requests.Single(r => r.Url.Contains("/search/jql", StringComparison.Ordinal)).Body!)
            .Should().Be("project = \"SHOP\" AND assignee = currentUser() AND status in (\"To Do\", \"Selected for Development\") ORDER BY key ASC");
    }

    [Fact]
    public async Task Tracking_ReadsKnownKeys_WhateverTheirStatus_AndComparesTheAssignee()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", body => IsTracking(body)
                ? Json($$"""{"issues":[{{Issue("SHOP-7", "SHOP-100", status: "In Progress", category: "indeterminate")}},{{Issue("SHOP-8", "SHOP-100", assignee: "acc-priya")}}],"isLast":true}""")
                : Json($$"""{"issues":[{{Issue("SHOP-1", "SHOP-100")}}],"isLast":true}"""));
        var client = new JiraCloudClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential, Assignment(known: ["SHOP-1", "SHOP-7", "shop-8"]), CancellationToken.None);

        tickets.Select(t => t.Key).Should().Equal("SHOP-1", "SHOP-7", "SHOP-8");
        var tracking = handler.Requests.Where(r => r.Body is { } b && r.Url.Contains("/search/jql", StringComparison.Ordinal) && IsTracking(b)).ToList();
        FakeTrackerHandler.JqlOf(tracking.Single().Body!).Should().Be("key in (\"SHOP-7\", \"SHOP-8\")",
            "SHOP-1 came with the intake query; keys are read in one batch");
        var moved = tickets.Single(t => t.Key == "SHOP-7");
        moved.InIntake.Should().BeFalse();
        moved.AssignedToMe.Should().BeTrue();
        moved.StatusCategory.Should().Be("inprogress");
        tickets.Single(t => t.Key == "SHOP-8").AssignedToMe.Should().BeFalse("it was reassigned to Priya");
    }

    [Fact]
    public async Task Tracking_ADeletedKey_IsDroppedAndTheBatchAskedAgain()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", body =>
            {
                var jql = FakeTrackerHandler.JqlOf(body);
                if (!IsTracking(body))
                    return Json("""{"issues":[],"isLast":true}""");
                return jql.Contains("SHOP-9", StringComparison.Ordinal)
                    ? (HttpStatusCode.BadRequest, """{"errorMessages":["An issue with key 'SHOP-9' does not exist for field 'key'."],"errors":{}}""", "application/json")
                    : Json($$"""{"issues":[{{Issue("SHOP-7", "SHOP-100")}}],"isLast":true}""");
            });
        var client = new JiraCloudClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential, Assignment(known: ["SHOP-7", "SHOP-9"]), CancellationToken.None);

        tickets.Select(t => t.Key).Should().Equal(["SHOP-7"], "SHOP-9's absence is how the host learns it is gone");
        JiraQueries.MissingKeys("An issue with key 'SHOP-9' does not exist for field 'key'. An issue with key 'shop-10' does not exist.")
            .Should().Equal("SHOP-9", "SHOP-10");
    }

    [Fact]
    public async Task AStorysSubTasks_AreListedInOrder_AndReadAlong_ButOnlyInIntakeIfTheyMatchIt()
    {
        var subtasks = """[{"key":"SHOP-22"},{"key":"SHOP-21"}]""";
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", body => IsTracking(body)
                ? Json($$"""{"issues":[{{Issue("SHOP-21", "SHOP-20", parentType: "Story", parentLevel: 0, type: "Sub-task", level: -1, status: "In Progress", category: "indeterminate")}},{{Issue("SHOP-22", "SHOP-20", parentType: "Story", parentLevel: 0, type: "Sub-task", level: -1, assignee: "acc-priya")}}],"isLast":true}""")
                : Json($$"""{"issues":[{{Issue("SHOP-20", "SHOP-100", subtasks: subtasks)}}],"isLast":true}"""));
        var client = new JiraCloudClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential, Assignment(scopeMode: TrackerScopeModes.All), CancellationToken.None);

        var story = tickets.Single(t => t.Key == "SHOP-20");
        story.ChildKeys.Should().Equal(["SHOP-22", "SHOP-21"], "Jira's sub-task order");
        story.InIntake.Should().BeTrue();
        var sub = tickets.Single(t => t.Key == "SHOP-21");
        sub.InIntake.Should().BeFalse();
        sub.ParentKey.Should().Be("SHOP-20");
        sub.ParentType.Should().Be("Story");
        sub.EpicKey.Should().BeNull("its parent is a story, not an epic");
        // What the host decides Story membership on: a tracked sub-task's own assignee and status category.
        sub.AssignedToMe.Should().BeTrue("assigned to the token's owner");
        sub.StatusCategory.Should().Be("inprogress");
        var teammates = tickets.Single(t => t.Key == "SHOP-22");
        teammates.AssignedToMe.Should().BeFalse("assigned to someone else");
        teammates.StatusCategory.Should().Be("todo");
    }

    [Fact]
    public async Task Fetch_WithDescendants_ResolvesEachTicketToItsConfiguredEpic()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", body =>
            {
                var jql = FakeTrackerHandler.JqlOf(body);
                if (!jql.Contains("assignee", StringComparison.Ordinal))
                    return Json("""{"issues":[{"key":"SHOP-200","fields":{"parent":{"key":"SHOP-100"}}}],"isLast":true}""");
                return Json($$"""{"issues":[{{Issue("SHOP-1", "SHOP-100")}},{{Issue("SHOP-2", "SHOP-200", parentType: "Feature", parentLevel: 0)}},{{Issue("SHOP-3", "OTHER-9", parentType: "Story", parentLevel: 0)}}],"isLast":true}""");
            });
        var client = new JiraCloudClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential, Assignment(descendants: true), CancellationToken.None);

        tickets.Single(t => t.Key == "SHOP-1").EpicKey.Should().Be("SHOP-100", "a direct child");
        tickets.Single(t => t.Key == "SHOP-2").EpicKey.Should().Be("SHOP-100", "a grandchild, through its feature");
        tickets.Single(t => t.Key == "SHOP-3").EpicKey.Should().BeNull();
        var ticketQuery = handler.Requests.Select(r => r.Body).OfType<string>().Select(FakeTrackerHandler.JqlOf).Last();
        ticketQuery.Should().Contain("parent in (\"SHOP-100\", \"SHOP-200\")");
        tickets[0].AcceptanceCriteriaHtml.Should().BeNull("the site has no Acceptance Criteria field");
    }

    [Fact]
    public async Task TheAcceptanceCriteriaField_AndTheAccount_AreAskedOncePerSite()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", Fields)
            .On(HttpMethod.Post, "/rest/api/3/search/jql", """{"issues":[],"isLast":true}""");
        var client = new JiraCloudClient(new HttpClient(handler));

        await client.FetchTicketsAsync(Credential, Assignment(), CancellationToken.None);
        await client.FetchTicketsAsync(Credential, Assignment(), CancellationToken.None);

        handler.Requests.Count(r => r.Url.EndsWith("/rest/api/3/field", StringComparison.Ordinal)).Should().Be(1);
        handler.Requests.Count(r => r.Url.EndsWith("/rest/api/3/myself", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Comments_OnlyNewerThanTheHostsLast_OldestFirst()
    {
        var handler = new FakeTrackerHandler().On(HttpMethod.Get, "/rest/api/3/issue/SHOP-1/comment", """
            {"comments":[
              {"id":"3","author":{"displayName":"Ana"},"created":"2026-10-03T10:00:00.000+0000","renderedBody":"<p>third</p>"},
              {"id":"2","author":{"displayName":"Bo"},"created":"2026-10-02T10:00:00.000+0000","renderedBody":"<p>second</p>"},
              {"id":"1","author":{"displayName":"Ana"},"created":"2026-10-01T10:00:00.000+0000","renderedBody":"<p>first</p>"}]}
            """);
        var client = new JiraCloudClient(new HttpClient(handler));
        var ticket = new RawTicket("SHOP-1", "1", "u", null, "t", null, null, null, null, null, null, null, [], [], null);

        var comments = await client.FetchCommentsAsync(Credential, ticket,
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), 30, CancellationToken.None);

        comments.Select(c => c.Id).Should().Equal("2", "3");
        comments[1].Author.Should().Be("Ana");
        comments[1].BodyHtml.Should().Be("<p>third</p>");
        var url = handler.Requests.Single().Url;
        url.Should().Contain("orderBy=-created").And.Contain("maxResults=30").And.Contain("expand=renderedBody");
    }

    [Fact]
    public async Task Fetch_RefusesToReturnAPartialSet_WhenThePagingCapIsHit()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", Myself)
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", $$"""{"issues":[{{Issue("SHOP-1", "SHOP-100")}}],"nextPageToken":"more"}""");
        var client = new JiraCloudClient(new HttpClient(handler));

        var act = () => client.FetchTicketsAsync(Credential, Assignment(), CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("too many to poll");
    }

    [Theory]
    [InlineData("2026-10-01T09:30:00.000+0000", 9)]
    [InlineData("2026-10-01T06:30:00.000-0300", 9)]
    [InlineData("2026-10-01T09:30:00Z", 9)]
    public void JiraDates_WithCompactOffsets_Parse(string text, int utcHour)
        => JiraCloudClient.ParseDate(text)!.Value.UtcDateTime.Hour.Should().Be(utcHour);
}

public sealed class AdoTicketFetchTests
{
    private const string Pat = "ado-pat-0123456789abcdefghijklmnop";
    private static readonly TrackerCredential Credential = new("https://dev.azure.com/contoso", "", Pat);
    private const string ConnectionData = """
        {"authenticatedUser":{"id":"me-0001","providerDisplayName":"Dana","properties":{"Account":{"$value":"dana@contoso.com"}}}}
        """;

    private const string Types = """
        {"value":[
          {"name":"User Story","states":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"},{"name":"Closed","category":"Completed"}]},
          {"name":"Bug","states":[{"name":"New","category":"Proposed"},{"name":"Resolved","category":"Resolved"},{"name":"Closed","category":"Completed"}]},
          {"name":"Epic","states":[{"name":"New","category":"Proposed"},{"name":"Done","category":"Completed"}]}]}
        """;

    private const string Categories = """
        {"value":[{"referenceName":"Microsoft.RequirementCategory","workItemTypes":[{"name":"User Story"}]},
                  {"referenceName":"Microsoft.BugCategory","workItemTypes":[{"name":"Bug"}]}]}
        """;

    private static (HttpStatusCode, string, string) Json(string body) => (HttpStatusCode.OK, body, "application/json");

    private static FakeTrackerHandler Handler() => new FakeTrackerHandler()
        .On(HttpMethod.Get, "/_apis/connectionData", ConnectionData)
        .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", Types)
        .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", Categories)
        .On(HttpMethod.Post, "/_apis/wit/wiql", body => FakeTrackerHandler.WiqlOf(body).Contains("WorkItemLinks", StringComparison.Ordinal)
            ? Json("""
                {"workItemRelations":[
                  {"rel":null,"source":null,"target":{"id":1}},
                  {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":1},"target":{"id":2}},
                  {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":2},"target":{"id":3}},
                  {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":1},"target":{"id":4}}]}
                """)
            : Json("""{"workItems":[{"id":3},{"id":4}]}"""))
        .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", """
            {"value":[
              {"id":3,"fields":{"System.Title":"Fix the cart","System.WorkItemType":"Bug","System.State":"New",
                 "Microsoft.VSTS.Common.Priority":2,"System.Tags":"web; checkout","System.TeamProject":"Shop",
                 "System.AreaPath":"Shop\\Web","System.Description":"<div>desc</div>","Microsoft.VSTS.TCM.ReproSteps":"<ol><li>open</li></ol>",
                 "System.ChangedDate":"2026-10-02T11:00:00.123Z"},
               "relations":[
                 {"rel":"AttachedFile","url":"https://dev.azure.com/contoso/0b1c/_apis/wit/attachments/aaaa-1111","attributes":{"name":"trace.log","resourceSize":2048}},
                 {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/2"}]},
              {"id":4,"fields":{"System.Title":"Add coupon","System.WorkItemType":"User Story","System.State":"New",
                 "Microsoft.VSTS.Common.AcceptanceCriteria":"<p>coupon applies</p>","System.TeamProject":"Shop"}}]}
            """);

    [Fact]
    public async Task Intake_UnderTheEpics_RunsTheTwoStepWiql_ThenReadsWorkItemsWithRelations()
    {
        var handler = Handler();
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "ado-contoso", TrackerKinds.AzureDevOps, "Shop", ["1"], null, true, [],
                TrackerScopeModes.Epics),
            CancellationToken.None);

        tickets.Select(t => t.Key).Should().Equal("3", "4");
        tickets.Should().OnlyContain(t => t.InIntake && t.AssignedToMe);
        var bug = tickets[0];
        bug.EpicKey.Should().Be("1", "the bug sits under feature 2 under epic 1");
        bug.ParentKey.Should().Be("2");
        bug.AreaPath.Should().Be(@"Shop\Web");
        bug.Url.Should().Be("https://dev.azure.com/contoso/Shop/_workitems/edit/3");
        bug.Priority.Should().Be("2");
        bug.Labels.Should().Equal("web", "checkout");
        bug.StatusCategory.Should().Be("todo");
        bug.ReproStepsHtml.Should().Contain("<li>open</li>");
        bug.UpdatedAt.Should().Be(new DateTimeOffset(2026, 10, 2, 11, 0, 0, 123, TimeSpan.Zero));
        bug.Attachments.Should().ContainSingle().Which.Should().Be(new RawAttachment("aaaa-1111", "trace.log", 2048, null,
            "https://dev.azure.com/contoso/0b1c/_apis/wit/attachments/aaaa-1111"));
        tickets[1].EpicKey.Should().Be("1");
        tickets[1].AcceptanceCriteriaHtml.Should().Contain("coupon applies");

        var wiql = handler.Requests.Where(r => r.Url.Contains("/wiql", StringComparison.Ordinal)).Select(r => FakeTrackerHandler.WiqlOf(r.Body!)).ToList();
        wiql[0].Should().Contain("MODE (Recursive)");
        wiql[1].Should().Be(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me"
            + " AND [System.State] IN ('New') AND [System.WorkItemType] NOT IN ('Epic', 'Feature')"
            + " AND [System.Id] IN (2, 3, 4) ORDER BY [System.Id]");
        var batch = JsonDocument.Parse(handler.Requests.First(r => r.Url.Contains("workitemsbatch", StringComparison.Ordinal)).Body!).RootElement;
        batch.GetProperty("$expand").GetString().Should().Be("Relations");
        batch.TryGetProperty("fields", out _).Should().BeFalse("the API refuses fields together with an expand");
    }

    [Fact]
    public async Task AllMyTickets_TracksKnownIds_OrdersChildrenByRank_AndNamesTheEpicParent()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/_apis/connectionData", ConnectionData)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", Types)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", Categories)
            .On(HttpMethod.Post, "/_apis/wit/wiql", """{"workItems":[{"id":10}]}""")
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", body =>
            {
                var doc = JsonDocument.Parse(body).RootElement;
                var ids = doc.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).ToList();
                if (ids.SequenceEqual([10]))
                    return Json("""
                        {"value":[{"id":10,"fields":{"System.Title":"Checkout story","System.WorkItemType":"User Story","System.State":"New","System.TeamProject":"Shop"},
                          "relations":[
                            {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/11"},
                            {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/12"},
                            {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/5"}]}]}
                        """);
                if (ids.Contains(11))
                    // 20 was deleted: omitted (errorPolicy=Omit returns null for it).
                    return Json("""
                        {"value":[
                          null,
                          {"id":11,"fields":{"System.Title":"Task B","System.WorkItemType":"Task","System.State":"Active","System.TeamProject":"Shop",
                             "Microsoft.VSTS.Common.StackRank":2.0,"System.AssignedTo":{"id":"me-0001","uniqueName":"dana@contoso.com"}}},
                          {"id":12,"fields":{"System.Title":"Task A","System.WorkItemType":"Task","System.State":"New","System.TeamProject":"Shop",
                             "Microsoft.VSTS.Common.StackRank":1.0,"System.AssignedTo":{"id":"other","uniqueName":"priya@contoso.com"}}}]}
                        """);
                return Json("""{"value":[{"id":5,"fields":{"System.Title":"Big epic","System.WorkItemType":"Epic","System.State":"New"}}]}""");
            });
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "ado-contoso", TrackerKinds.AzureDevOps, "Shop", [], null, false,
                [new TrackerKnownTicket("20", null, null)], TrackerScopeModes.All, ["New", "Approved"]),
            CancellationToken.None);

        tickets.Select(t => t.Key).Should().BeEquivalentTo(["10", "11", "12"], "20 no longer exists");
        var story = tickets.Single(t => t.Key == "10");
        story.InIntake.Should().BeTrue();
        story.ChildKeys.Should().Equal(["12", "11"], "backlog rank, not link order");
        story.ParentKey.Should().Be("5");
        story.ParentType.Should().Be("Epic");
        story.EpicKey.Should().Be("5", "its parent is an Epic");
        tickets.Single(t => t.Key == "11").Should().Match<RawTicket>(t => !t.InIntake && t.AssignedToMe && t.ParentKey == null);
        tickets.Single(t => t.Key == "12").AssignedToMe.Should().BeFalse();
        var intakeWiql = FakeTrackerHandler.WiqlOf(handler.Requests.Single(r => r.Url.Contains("/wiql", StringComparison.Ordinal)).Body!);
        intakeWiql.Should().Contain("[System.State] IN ('New', 'Approved')").And.NotContain("[System.Id] IN");
        var trackingBatch = JsonDocument.Parse(handler.Requests.Where(r => r.Url.Contains("workitemsbatch", StringComparison.Ordinal)).ElementAt(1).Body!).RootElement;
        trackingBatch.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).Should().BeEquivalentTo([20, 11, 12]);
    }

    [Fact]
    public async Task AStorysTrackedTasks_CarryTheirOwnAssignee_AndStateCategory()
    {
        const string types = """
            {"value":[
              {"name":"User Story","states":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"},{"name":"Closed","category":"Completed"}]},
              {"name":"Task","states":[{"name":"To Do","category":"Proposed"},{"name":"In Progress","category":"InProgress"},{"name":"In Test","category":"Resolved"},{"name":"Done","category":"Completed"}]}]}
            """;
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/_apis/connectionData", ConnectionData)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", types)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", Categories)
            .On(HttpMethod.Post, "/_apis/wit/wiql", """{"workItems":[{"id":10}]}""")
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", body =>
            {
                var ids = JsonDocument.Parse(body).RootElement.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).ToList();
                return ids.SequenceEqual([10])
                    ? Json("""
                        {"value":[{"id":10,"fields":{"System.Title":"Checkout story","System.WorkItemType":"User Story","System.State":"New","System.TeamProject":"Shop"},
                          "relations":[
                            {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/11"},
                            {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/12"},
                            {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/13"}]}]}
                        """)
                    : Json("""
                        {"value":[
                          {"id":11,"fields":{"System.Title":"Mine","System.WorkItemType":"Task","System.State":"To Do","System.TeamProject":"Shop",
                             "System.AssignedTo":{"id":"ME-0001","uniqueName":"someone-else@contoso.com"}}},
                          {"id":12,"fields":{"System.Title":"QA's now","System.WorkItemType":"Task","System.State":"In Test","System.TeamProject":"Shop",
                             "System.AssignedTo":{"id":"qa-team","uniqueName":"qa@contoso.com"}}},
                          {"id":13,"fields":{"System.Title":"Unassigned","System.WorkItemType":"Task","System.State":"To Do","System.TeamProject":"Shop"}}]}
                        """);
            });
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var tickets = await client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "ado-contoso", TrackerKinds.AzureDevOps, "Shop", [], null, false, [], TrackerScopeModes.All, ["New"]),
            CancellationToken.None);

        tickets.Single(t => t.Key == "10").AssignedToMe.Should().BeTrue("the intake query is @Me");
        tickets.Single(t => t.Key == "11").Should().Match<RawTicket>(t => t.AssignedToMe && !t.InIntake && t.StatusCategory == "todo",
            "matched on the identity id, case-insensitively");
        tickets.Single(t => t.Key == "12").Should().Match<RawTicket>(t => !t.AssignedToMe && t.StatusCategory == "inprogress",
            "a Resolved-category state is under way");
        tickets.Single(t => t.Key == "13").AssignedToMe.Should().BeFalse("nobody is assigned");
    }

    [Fact]
    public void Children_AreOrderedByRank_UnrankedLast_ThenById()
        => AdoQueries.OrderChildren([(7, null), (5, 2.5), (6, null), (9, 1.0)]).Should().Equal(9, 5, 6, 7);

    [Fact]
    public async Task Comments_UseTheWorkItemsProject_SkipDeleted_AndKeepOnlyNewer()
    {
        var handler = new FakeTrackerHandler().On(HttpMethod.Get, "/Shop/_apis/wit/workItems/3/comments", """
            {"comments":[
              {"id":12,"text":"x","renderedText":"<p>newest</p>","createdBy":{"displayName":"Ana"},"createdDate":"2026-10-03T10:00:00Z"},
              {"id":11,"isDeleted":true,"text":"gone","createdBy":{"displayName":"Bo"},"createdDate":"2026-10-02T10:00:00Z"},
              {"id":10,"text":"old","createdBy":{"displayName":"Bo"},"createdDate":"2026-09-01T10:00:00Z"}]}
            """);
        var client = new AzureDevOpsClient(new HttpClient(handler));
        var ticket = new RawTicket("3", "3", "https://dev.azure.com/contoso/Shop/_workitems/edit/3", null, "t",
            null, null, null, null, null, null, null, [], [], null);

        var comments = await client.FetchCommentsAsync(Credential, ticket,
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), 30, CancellationToken.None);

        comments.Should().ContainSingle().Which.Should().Be(new RawComment("12", "Ana",
            new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero), "<p>newest</p>"));
        handler.Requests.Single().Url.Should().Contain("api-version=7.1-preview.4").And.Contain("order=desc").And.Contain("$top=30");
    }
}

/// <summary>Serves attachment bytes and redirects; records each request's URL and auth.</summary>
internal sealed class FakeDownloadHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<(string Url, string? Authorization)> Requests { get; } = [];

    public FakeDownloadHandler Bytes(string url, int length, string mediaType = "application/octet-stream")
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Enumerable.Repeat((byte)'x', length).ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue(mediaType) },
            },
        };
        return this;
    }

    public FakeDownloadHandler Redirect(string url, string location)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.SeeOther) { Headers = { Location = new Uri(location) } };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add((url, request.Headers.Authorization?.ToString()));
        return Task.FromResult(_routes.TryGetValue(url, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class TrackerAttachmentCacheTests : IDisposable
{
    private const string Token = "ATATT3xFfGF0-secret-jira-token";
    private const string Site = "https://acme.atlassian.net";
    private static readonly TrackerCredential Credential = new(Site, "dana@acme.com", Token);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-tickets-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private TrackerAttachmentCache Cache(FakeDownloadHandler handler)
        => new(_root, new HttpClient(handler), NullLogger<TrackerAttachmentCache>.Instance, _clock);

    private static RawAttachment Jira(string id, string name, long size, string? mime = "image/png")
        => new(id, name, size, mime, $"{Site}/rest/api/3/attachment/content/{id}");

    private async Task<TrackerAttachmentMeta[]> EnsureAsync(TrackerAttachmentCache cache, params RawAttachment[] attachments)
        => await cache.EnsureAsync(Credential, "jira-acme", "SHOP-1", attachments, TrackerAttachmentCache.PlanNames(attachments), CancellationToken.None);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("a<b>:c|d?.png", "a_b__c_d_.png")]
    [InlineData("", "attachment")]
    [InlineData("..", "attachment")]
    [InlineData(".env", "env")]
    [InlineData("trailing. ", "trailing")]
    public void SafeFileName_StaysInsideItsFolder(string input, string expected)
        => TrackerAttachmentCache.SafeFileName(input).Should().Be(expected);

    [Fact]
    public void SafeFileName_CapsLength_KeepingTheExtension()
    {
        var name = TrackerAttachmentCache.SafeFileName(new string('n', 300) + ".png");
        name.Length.Should().Be(120);
        name.Should().EndWith(".png");
    }

    [Fact]
    public void PlanNames_DeDuplicatesCollisions_CaseInsensitively()
    {
        var names = TrackerAttachmentCache.PlanNames(
            [Jira("1", "shot.png", 1), Jira("2", "Shot.png", 1), Jira("3", "dir/shot.png", 1), Jira("4", "notes", 1), Jira("5", "notes", 1)]);

        names.Select(n => n.FileName).Should().Equal("shot.png", "Shot (2).png", "shot (3).png", "notes", "notes (2)");
        names[2].OriginalName.Should().Be("dir/shot.png");
    }

    [Theory]
    [InlineData("demo.mp4", null, "video")]
    [InlineData("clip.bin2", "video/quicktime", "video")]
    [InlineData("logs.zip", null, "type")]
    [InlineData("bundle", "application/x-7z-compressed", "type")]
    [InlineData("setup.exe", null, "type")]
    [InlineData("screen.png", "image/png", null)]
    [InlineData("deploy.sh", "text/x-sh", null)]
    public void SkipReason_ListsButNeverDownloads_VideosArchivesAndExecutables(string name, string? mime, string? reason)
        => TrackerAttachmentCache.SkipReason(new RawAttachment("1", name, 10, mime, "u")).Should().Be(reason);

    [Fact]
    public async Task Downloads_WithTheToken_AndDoesNotRedownloadAnUnchangedFile()
    {
        var handler = new FakeDownloadHandler().Bytes($"{Site}/rest/api/3/attachment/content/1", 100);
        var cache = Cache(handler);

        var metas = await EnsureAsync(cache, Jira("1", "shot.png", 100));
        await EnsureAsync(cache, Jira("1", "shot.png", 100));

        metas.Should().Equal(new TrackerAttachmentMeta("shot.png", 100, "image/png", true));
        var file = Path.Combine(_root, "jira-acme", "SHOP-1", "shot.png");
        new FileInfo(file).Length.Should().Be(100);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Authorization.Should().StartWith("Basic ");
        Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName).Should().Equal("shot.png");
    }

    [Fact]
    public async Task ASizeChange_Redownloads_AndARemovedAttachment_IsDeleted()
    {
        var handler = new FakeDownloadHandler()
            .Bytes($"{Site}/rest/api/3/attachment/content/1", 100)
            .Bytes($"{Site}/rest/api/3/attachment/content/2", 10);
        var cache = Cache(handler);
        await EnsureAsync(cache, Jira("1", "shot.png", 100), Jira("2", "old.txt", 10, "text/plain"));

        handler.Bytes($"{Site}/rest/api/3/attachment/content/1", 150);
        await EnsureAsync(cache, Jira("1", "shot.png", 150));

        var dir = Path.Combine(_root, "jira-acme", "SHOP-1");
        new FileInfo(Path.Combine(dir, "shot.png")).Length.Should().Be(150);
        File.Exists(Path.Combine(dir, "old.txt")).Should().BeFalse();
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task SkippedTypes_AndOversizedFiles_AreListedButNeverRequested()
    {
        var handler = new FakeDownloadHandler();
        var cache = Cache(handler);

        var metas = await EnsureAsync(cache,
            Jira("1", "demo.mp4", 1000, "video/mp4"),
            Jira("2", "logs.zip", 1000, "application/zip"),
            Jira("3", "huge.png", TrackerAttachmentCache.MaxFileBytes + 1));

        metas.Should().OnlyContain(m => !m.Cached);
        metas.Select(m => m.FileName).Should().Equal("demo.mp4", "logs.zip", "huge.png");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePerTicketCap_LeavesLaterFilesUncached()
    {
        var handler = new FakeDownloadHandler();
        var size = 24L * 1024 * 1024;
        var attachments = Enumerable.Range(1, 5).Select(i => Jira($"{i}", $"part{i}.png", size)).ToArray();
        foreach (var a in attachments)
            handler.Bytes(a.DownloadUrl, 16);
        var cache = Cache(handler);

        var metas = await EnsureAsync(cache, attachments);

        metas.Select(m => m.Cached).Should().Equal(true, true, true, true, false);
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task ARedirectOffTheSite_IsFollowed_WithoutTheToken()
    {
        var media = "https://api.media.atlassian.com/file/abc/binary?token=signed";
        var handler = new FakeDownloadHandler()
            .Redirect($"{Site}/rest/api/3/attachment/content/1", media)
            .Bytes(media, 42);
        var cache = Cache(handler);

        var metas = await EnsureAsync(cache, Jira("1", "shot.png", 42));

        metas.Single().Cached.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Authorization.Should().NotBeNull();
        handler.Requests[1].Authorization.Should().BeNull("the token never leaves the tracker's own host");
    }

    [Fact]
    public async Task AnAttachmentUrlOffTheSite_IsNeverRequested()
    {
        var handler = new FakeDownloadHandler().Bytes("https://evil.example.com/a.png", 5);
        var cache = Cache(handler);

        var metas = await EnsureAsync(cache, new RawAttachment("1", "a.png", 5, null, "https://evil.example.com/a.png"));

        metas.Single().Cached.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ASignInPageInsteadOfTheFile_IsNotCached()
    {
        var handler = new FakeDownloadHandler().Bytes($"{Site}/rest/api/3/attachment/content/1", 30, "text/html");
        var cache = Cache(handler);

        var metas = await EnsureAsync(cache, Jira("1", "shot.png", 30));

        metas.Single().Cached.Should().BeFalse();
        File.Exists(Path.Combine(_root, "jira-acme", "SHOP-1", "shot.png")).Should().BeFalse();
    }

    [Fact]
    public async Task Prune_RemovesTicketsNotSeenFor14Days_AndKeepsTheRest()
    {
        var handler = new FakeDownloadHandler()
            .Bytes($"{Site}/rest/api/3/attachment/content/1", 5)
            .Bytes($"{Site}/rest/api/3/attachment/content/2", 5);
        var cache = Cache(handler);
        await cache.EnsureAsync(Credential, "jira-acme", "SHOP-1", [Jira("1", "a.png", 5)], TrackerAttachmentCache.PlanNames([Jira("1", "a.png", 5)]), CancellationToken.None);
        await cache.EnsureAsync(Credential, "jira-acme", "SHOP-2", [Jira("2", "b.png", 5)], TrackerAttachmentCache.PlanNames([Jira("2", "b.png", 5)]), CancellationToken.None);
        cache.MarkSeen("jira-acme", "SHOP-1");
        cache.MarkSeen("jira-acme", "SHOP-2");

        _clock.Now += TimeSpan.FromDays(10);
        cache.MarkSeen("jira-acme", "SHOP-2");
        _clock.Now += TimeSpan.FromDays(5);

        cache.Prune().Should().Be(1);
        Directory.Exists(Path.Combine(_root, "jira-acme", "SHOP-1")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "jira-acme", "SHOP-2")).Should().BeTrue();
    }

    [Fact]
    public async Task CopyToWorktree_CopiesTheFiles_IntoOrkkaContextAttachments()
    {
        var handler = new FakeDownloadHandler().Bytes($"{Site}/rest/api/3/attachment/content/1", 7);
        var cache = Cache(handler);
        await EnsureAsync(cache, Jira("1", "shot.png", 7));
        cache.MarkSeen("jira-acme", "SHOP-1");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(worktree, ".orkka", "context", "attachments"));
        File.WriteAllText(Path.Combine(worktree, ".orkka", "context", "attachments", "shot.png"), "stale");

        cache.CopyToWorktree("jira-acme", "SHOP-1", worktree).Should().Be(1);
        cache.CopyToWorktree("jira-acme", "SHOP-404", worktree).Should().BeNull();

        var target = Path.Combine(worktree, ".orkka", "context", "attachments");
        Directory.GetFiles(target).Select(Path.GetFileName).Should().Equal("shot.png");
        new FileInfo(Path.Combine(target, "shot.png")).Length.Should().Be(7, "same names are overwritten");
    }
}

public sealed class TrackerPollerTests : IDisposable
{
    private const string Token = "ATATT3xFfGF0-poller-secret-token";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-poller-" + Guid.NewGuid().ToString("N"));
    private readonly TrackerStore _store;
    private readonly FileSecretStore _secrets;
    private readonly FakeSource _source = new();
    private readonly TrackerAttachmentCache _cache;
    private readonly FakeHub _hub = new();
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));

    public TrackerPollerTests()
    {
        _store = new TrackerStore(_root);
        _secrets = new FileSecretStore(Path.Combine(_root, "secrets"));
        _store.Upsert(new TrackerConnectionEntry
        {
            Id = "jira-acme", Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", Email = "dana@acme.com",
            Healthy = true, AddedAt = DateTimeOffset.UtcNow,
        });
        _secrets.Set("jira-acme", Token);
        _cache = new TrackerAttachmentCache(Path.Combine(_root, "tickets"), new HttpClient(new FakeDownloadHandler()),
            NullLogger<TrackerAttachmentCache>.Instance, _clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private TrackerTicketPoller Poller(TimeSpan? timeout = null) => new(_store, _secrets, _ => _source, _cache,
        NullLogger<TrackerTicketPoller>.Instance, _clock) { Timeout = timeout ?? TrackerTicketPoller.AssignmentTimeout };

    private Func<TrackerPrCheck, CancellationToken, Task<RunnerOpResult>> _prState =
        (_, _) => Task.FromResult(RunnerOpResult.Ok("open"));
    private readonly FakeWriter _writer = new();

    private TrackerWriteBack WriteBack(TimeSpan? prBudget = null) => new(_store, _secrets, _ => _writer, (c, ct) => _prState(c, ct),
        NullLogger<TrackerWriteBack>.Instance) { PrCheckBudget = prBudget ?? TrackerWriteBack.DefaultPrCheckBudget };

    private TrackerPollerService Service(TrackerTicketPoller? poller = null, TrackerWriteBack? writeBack = null)
        => new(_hub, _store, poller ?? Poller(), writeBack ?? WriteBack(), _cache, NullLogger<TrackerPollerService>.Instance, _clock);

    private static TrackerTransition Move(string id, string key, string target = "In Progress", string connection = "jira-acme")
        => new(id, connection, key, key, target, "Started");

    [Fact]
    public async Task Transitions_AreAppliedBeforeTheAssignments_AndAcknowledgedInOneCall()
    {
        _source.Tickets["SHOP"] = [Raw("SHOP-1")];
        _hub.Assignments = [Assignment("cfg", "SHOP")];
        _hub.Transitions = [Move("t1", "SHOP-12"), Move("t2", "SHOP-13", connection: "jira-gone")];
        _hub.PrChecks = [new TrackerPrCheck("p1", "https://github.com/acme/shop.git", "github", null, "https://github.com/acme/shop/pull/7")];
        _writer.Failures["SHOP-12"] = null;

        await Service().RunOnceAsync(CancellationToken.None);

        _hub.Order.Should().Equal("sync", "ack", "report");
        var ack = _hub.TransitionReports.Single();
        ack.InstanceId.Should().Be(_hub.InstanceId);
        ack.Transitions.Should().Equal(
            new TrackerTransitionAck("t1", true),
            new TrackerTransitionAck("t2", false, "This machine no longer holds that tracker connection."));
        ack.PrStates.Should().Equal(new TrackerPrState("p1", "open"));
        _writer.Calls.Should().Equal(("SHOP-12", "In Progress"));
    }

    [Fact]
    public async Task AFailedMove_IsAckedWithTheTrackersWords_WithoutTheToken()
    {
        _hub.Transitions = [Move("t1", "SHOP-12")];
        _writer.Failures["SHOP-12"] = new TrackerException($"No transition to 'In Progress' from 'Done' (token {Token}).");

        await Service().RunOnceAsync(CancellationToken.None);

        var ack = _hub.TransitionReports.Single().Transitions.Single();
        ack.Ok.Should().BeFalse();
        ack.Error.Should().Contain("No transition to 'In Progress'").And.NotContain(Token);
    }

    [Fact]
    public async Task NothingDue_SendsNoAck()
    {
        _hub.Assignments = [Assignment("cfg", "SHOP")];

        await Service().RunOnceAsync(CancellationToken.None);

        _hub.TransitionReports.Should().BeEmpty();
        _hub.Order.Should().Equal("sync", "report");
    }

    [Fact]
    public async Task PrChecks_MapStatesAndErrors()
    {
        _prState = (check, _) => Task.FromResult(check.CheckId switch
        {
            "merged" => RunnerOpResult.Ok("merged"),
            "closed" => RunnerOpResult.Ok("closed"),
            "weird" => RunnerOpResult.Ok("unknown"),
            _ => RunnerOpResult.Fail("gh is not signed in"),
        });
        var sync = new TrackerSyncResponse([], null,
            [Check("merged"), Check("closed"), Check("weird"), Check("failed")]);

        var report = await WriteBack().ApplyAsync(sync, "inst", CancellationToken.None);

        report!.Transitions.Should().BeEmpty();
        report.PrStates.Should().Equal(
            new TrackerPrState("merged", "merged"),
            new TrackerPrState("closed", "closed"),
            new TrackerPrState("weird", null, "The forge CLI reported an unexpected state 'unknown'."),
            new TrackerPrState("failed", null, "gh is not signed in"));
    }

    [Fact]
    public async Task PrChecks_ShareOneTimeBudget_AndUnfinishedOnesAreOmitted()
    {
        _prState = async (check, ct) =>
        {
            if (check.CheckId != "fast")
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return RunnerOpResult.Ok("open");
        };
        var sync = new TrackerSyncResponse([], null, [Check("fast"), Check("slow"), Check("never")]);

        var started = DateTime.UtcNow;
        var report = await WriteBack(TimeSpan.FromMilliseconds(200)).ApplyAsync(sync, "inst", CancellationToken.None);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
        report!.PrStates.Should().Equal(new TrackerPrState("fast", "open"));
    }

    [Fact]
    public async Task UnknownForge_IsAnError_NotAThrow()
    {
        var lookup = TrackerWriteBack.ViaForgeCli(new ForgeCli(NullLogger<ForgeCli>.Instance));

        var result = await lookup(new TrackerPrCheck("p", "https://example.com/r.git", "sourcehut", null, "u"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("sourcehut");
    }

    private static TrackerPrCheck Check(string id) => new(id, "https://github.com/acme/shop.git", "github", null, $"https://github.com/acme/shop/pull/{id}");

    private sealed class FakeWriter : ITrackerStatusWriter
    {
        public Dictionary<string, Exception?> Failures { get; } = new();
        public List<(string Key, string Target)> Calls { get; } = [];

        public Task<bool> TransitionAsync(TrackerCredential credential, string ticketKey, string ticketId, string targetStatus, CancellationToken ct)
        {
            Calls.Add((ticketKey, targetStatus));
            return Failures.GetValueOrDefault(ticketKey) is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(true);
        }
    }

    private static TrackerPollAssignment Assignment(string configId, string scope, params TrackerKnownTicket[] known)
        => new(configId, "jira-acme", TrackerKinds.Jira, scope, [$"{scope}-100"], null, false, known);

    private static RawTicket Raw(string key, DateTimeOffset? updated = null, string title = "A ticket")
        => new(key, key, $"https://acme.atlassian.net/browse/{key}", null, title, "Story", "To Do", "todo", "High",
            "<p>body</p>", null, null, [], [], updated ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task OneAssignmentsFailure_DoesNotStopTheOthers_AndCarriesNoTickets()
    {
        _source.Tickets["SHOP"] = [Raw("SHOP-1"), Raw("SHOP-2")];
        _source.Failures["CRM"] = new TrackerException($"Jira rejected the query (token {Token})");
        _hub.Assignments = [Assignment("cfg-crm", "CRM"), Assignment("cfg-shop", "SHOP")];

        (await Service().RunOnceAsync(CancellationToken.None)).Should().Be(2);

        _hub.Reports.Should().HaveCount(2);
        var failed = _hub.Reports.Single(r => r.ConfigId == "cfg-crm");
        failed.Ok.Should().BeFalse();
        failed.Tickets.Should().BeEmpty("a failed poll says nothing about membership");
        failed.Error.Should().Contain("rejected the query").And.NotContain(Token);
        var ok = _hub.Reports.Single(r => r.ConfigId == "cfg-shop");
        ok.Ok.Should().BeTrue();
        ok.Tickets.Select(t => t.Key).Should().Equal("SHOP-1", "SHOP-2");
        ok.InstanceId.Should().Be(_hub.InstanceId);
        _hub.SyncRequests.Single().ConnectionIds.Should().Equal("jira-acme");
        _hub.SyncRequests.Single().InstanceId.Should().Be(_hub.InstanceId);
    }

    [Fact]
    public async Task AFailureMidway_NeverReportsAPartialSetAsOk()
    {
        _source.Tickets["SHOP"] = [Raw("SHOP-1"), Raw("SHOP-2")];
        _source.CommentFailureFor = "SHOP-2";

        var outcome = await Poller().PollAsync(Assignment("cfg", "SHOP"), "inst", CancellationToken.None);

        outcome.Report.Ok.Should().BeFalse();
        outcome.Report.Tickets.Should().BeEmpty();
    }

    [Fact]
    public async Task UnchangedTickets_AreListedWithANullBody()
    {
        _source.Tickets["SHOP"] = [Raw("SHOP-1"), Raw("SHOP-2")];
        var poller = Poller();
        var first = await poller.PollAsync(Assignment("cfg", "SHOP"), "inst", CancellationToken.None);
        var hash1 = first.Report.Tickets[0].ContentHash;

        _source.Tickets["SHOP"] = [Raw("SHOP-1"), Raw("SHOP-2", title: "Renamed")];
        var second = await poller.PollAsync(Assignment("cfg", "SHOP",
            new TrackerKnownTicket("SHOP-1", hash1, null),
            new TrackerKnownTicket("SHOP-2", first.Report.Tickets[1].ContentHash, null)), "inst", CancellationToken.None);

        second.Report.Tickets.Select(t => t.Key).Should().Equal("SHOP-1", "SHOP-2");
        second.Report.Tickets[0].Body.Should().BeNull();
        second.Report.Tickets[0].ContentHash.Should().Be(hash1);
        second.Report.Tickets[1].Body!.Title.Should().Be("Renamed");
        second.Summary.Should().Be("SHOP: 2 tickets, 0 new, 1 changed");
        first.Summary.Should().Be("SHOP: 2 tickets, 2 new, 0 changed");
    }

    [Fact]
    public async Task Comments_AreFetchedForNewOrMovedTickets_AndOnlyAfterTheHostAccepted()
    {
        var known = new TrackerKnownTicket("SHOP-1", "h", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        _source.Tickets["SHOP"] = [Raw("SHOP-1")];
        _hub.Assignments = [Assignment("cfg", "SHOP", known)];
        var service = Service();

        _hub.Accept = false;
        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        _source.CommentCalls.Should().HaveCount(2, "a report the host did not accept delivered nothing");
        _source.CommentCalls[0].After.Should().Be(known.LastCommentAt);

        _hub.Accept = true;
        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        _source.CommentCalls.Should().HaveCount(3, "unchanged since the accepted report");

        _source.Tickets["SHOP"] = [Raw("SHOP-1", updated: DateTimeOffset.UtcNow)];
        await service.RunOnceAsync(CancellationToken.None);
        _source.CommentCalls.Should().HaveCount(4, "the ticket's updated time moved");
        _hub.Reports.Last().Tickets.Single().NewComments.Single().Body.Should().Be("**hi**");
    }

    [Fact]
    public async Task AHostWithoutTheMethods_IsLeftAloneForTenMinutes()
    {
        _hub.SyncFailure = new Exception("Failed to invoke 'SyncTrackers' due to an error on the server. Unknown hub method 'SyncTrackers'");
        var service = Service();

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        _hub.SyncRequests.Should().HaveCount(1);

        _clock.Now += TrackerPollerService.MissingMethodBackoff + TimeSpan.FromSeconds(1);
        await service.RunOnceAsync(CancellationToken.None);
        _hub.SyncRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoConnection_OrNoHub_MeansNoPolling()
    {
        _hub.Connected = false;
        await Service().RunOnceAsync(CancellationToken.None);
        _hub.SyncRequests.Should().BeEmpty();

        _hub.Connected = true;
        _store.Remove("jira-acme");
        await Service().RunOnceAsync(CancellationToken.None);
        _hub.SyncRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ASlowTracker_TimesOut_AsAFailedReport()
    {
        _source.Delay = TimeSpan.FromSeconds(10);

        var outcome = await Poller(TimeSpan.FromMilliseconds(100)).PollAsync(Assignment("cfg", "SHOP"), "inst", CancellationToken.None);

        outcome.Report.Ok.Should().BeFalse();
        outcome.Report.Error.Should().Contain("did not finish");
        outcome.Report.Tickets.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingToken_FailsTheReport_InWords()
    {
        _secrets.Delete("jira-acme");

        var outcome = await Poller().PollAsync(Assignment("cfg", "SHOP"), "inst", CancellationToken.None);

        outcome.Report.Ok.Should().BeFalse();
        outcome.Report.Error.Should().Contain("token for 'jira-acme' is missing");
    }

    [Theory]
    [InlineData("Unknown hub method 'ReportTickets'", true)]
    [InlineData("Method does not exist.", true)]
    [InlineData("The connection was closed", false)]
    public void MissingMethod_IsRecognized(string message, bool missing)
        => TrackerPollerService.IsMissingMethod(new Exception("outer", new Exception(message))).Should().Be(missing);

    private sealed class FakeSource : ITrackerTicketSource
    {
        public Dictionary<string, RawTicket[]> Tickets { get; } = new();
        public Dictionary<string, Exception> Failures { get; } = new();
        public string? CommentFailureFor { get; set; }
        public TimeSpan Delay { get; set; }
        public List<(string Key, DateTimeOffset? After)> CommentCalls { get; } = [];

        public async Task<IReadOnlyList<RawTicket>> FetchTicketsAsync(TrackerCredential credential, TrackerPollAssignment assignment, CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            if (Failures.TryGetValue(assignment.Scope, out var failure))
                throw failure;
            return Tickets.GetValueOrDefault(assignment.Scope) ?? [];
        }

        public Task<IReadOnlyList<RawComment>> FetchCommentsAsync(TrackerCredential credential, RawTicket ticket,
            DateTimeOffset? after, int max, CancellationToken ct)
        {
            CommentCalls.Add((ticket.Key, after));
            if (ticket.Key == CommentFailureFor)
                throw new TrackerException("Jira answered HTTP 500 for the comments.");
            max.Should().Be(TrackerTicketPoller.MaxComments);
            return Task.FromResult<IReadOnlyList<RawComment>>([new RawComment("c1", "Ana", DateTimeOffset.UtcNow, "<p><b>hi</b></p>")]);
        }
    }

    private sealed class FakeHub : ITrackerHub
    {
        public bool Connected { get; set; } = true;
        public bool Accept { get; set; } = true;
        public Exception? SyncFailure { get; set; }
        public TrackerPollAssignment[] Assignments { get; set; } = [];
        public TrackerTransition[]? Transitions { get; set; }
        public TrackerPrCheck[]? PrChecks { get; set; }
        public List<TrackerSyncRequest> SyncRequests { get; } = [];
        public List<TrackerTicketReport> Reports { get; } = [];
        public List<TrackerTransitionReport> TransitionReports { get; } = [];
        public List<string> Order { get; } = [];

        public bool IsConnected => Connected;
        public string InstanceId => "instance-1";

        public Task<TrackerSyncResponse?> SyncAsync(TrackerSyncRequest request, CancellationToken ct)
        {
            SyncRequests.Add(request);
            Order.Add("sync");
            if (SyncFailure is not null)
                throw SyncFailure;
            return Task.FromResult<TrackerSyncResponse?>(new TrackerSyncResponse(Assignments, Transitions, PrChecks));
        }

        public Task<TrackerReportAck?> AckTransitionsAsync(TrackerTransitionReport report, CancellationToken ct)
        {
            TransitionReports.Add(report);
            Order.Add("ack");
            return Task.FromResult<TrackerReportAck?>(new TrackerReportAck(true));
        }

        public Task<TrackerReportAck?> ReportAsync(TrackerTicketReport report, CancellationToken ct)
        {
            Reports.Add(report);
            Order.Add("report");
            return Task.FromResult<TrackerReportAck?>(new TrackerReportAck(Accept, Accept ? null : "lease lost"));
        }
    }
}

public sealed class TicketAttachmentRunCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-runcopy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private RunnerOperations Ops(TrackerAttachmentCache? cache)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:RepoBasePath"] = Path.Combine(_root, "cache") })
            .Build();
        return new RunnerOperations(
            Mock.Of<IGitWorkspaceManager>(),
            Mock.Of<IAiAgentInvoker>(),
            new ProviderDetection(Mock.Of<ILogger<ProviderDetection>>()),
            config,
            new RunnerHostLink(),
            Mock.Of<ILogger<RunnerOperations>>(),
            ticketCache: cache);
    }

    [Fact]
    public void ARunForAnImportedTicket_GetsItsCachedAttachments_AndAMissingCacheIsNotAnError()
    {
        var cache = new TrackerAttachmentCache(Path.Combine(_root, "tickets"), new HttpClient(new FakeDownloadHandler()),
            NullLogger<TrackerAttachmentCache>.Instance);
        var ticketDir = cache.TicketDir("ado-contoso", "42");
        Directory.CreateDirectory(ticketDir);
        File.WriteAllText(Path.Combine(ticketDir, "trace.log"), "boom");
        File.WriteAllText(Path.Combine(ticketDir, TrackerAttachmentCache.SeenMarker), "2026-10-26T00:00:00Z");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        var ops = Ops(cache);

        ops.CopyTicketAttachments(new TrackerTicketRef("ado-contoso", "42"), worktree);
        ops.CopyTicketAttachments(new TrackerTicketRef("ado-contoso", "999"), worktree);
        ops.CopyTicketAttachments(null, worktree);
        Ops(null).CopyTicketAttachments(new TrackerTicketRef("ado-contoso", "42"), worktree);

        var target = Path.Combine(worktree, ".orkka", "context", "attachments");
        Directory.GetFiles(target).Select(Path.GetFileName).Should().Equal("trace.log");
        File.ReadAllText(Path.Combine(target, "trace.log")).Should().Be("boom");
    }
}
