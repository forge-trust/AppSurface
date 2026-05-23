using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Renders encoded harvest progress HTML fragments for the AppSurface Docs observatory.
/// </summary>
/// <remarks>
/// The renderer accepts already-redacted progress snapshots and emits bounded markup for full pages and Turbo stream
/// updates. It HTML-encodes text and attribute values, truncates activity to eight entries and diagnostics to four, and
/// treats <see cref="AppSurfaceDocsHarvestRunState.Completed"/> and <see cref="AppSurfaceDocsHarvestRunState.Failed"/> as
/// terminal states. Callers should pass only app-relative return URLs that have already been validated.
/// </remarks>
internal static class AppSurfaceDocsHarvestProgressRenderer
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    /// <summary>
    /// Renders the observatory fragment for a harvest progress snapshot.
    /// </summary>
    /// <param name="snapshot">The redacted snapshot to render.</param>
    /// <param name="returnUrl">The app-relative URL used by the completion link and navigation data attributes.</param>
    /// <param name="completionDelayMilliseconds">The completion navigation delay in milliseconds.</param>
    /// <param name="includeReturnNavigation">Whether to emit the completion return link and return-url data attribute.</param>
    /// <returns>An encoded HTML fragment for the observatory surface.</returns>
    /// <remarks>
    /// When <paramref name="includeReturnNavigation"/> is <see langword="true"/> and the snapshot is completed, the
    /// fragment includes data attributes used by the client script to navigate after the configured delay. Failed runs
    /// render diagnostics but do not auto-navigate. A blank <paramref name="returnUrl"/> falls back to <c>/</c>.
    /// </remarks>
    internal static string Render(
        AppSurfaceDocsHarvestProgressSnapshot snapshot,
        string returnUrl,
        int completionDelayMilliseconds,
        bool includeReturnNavigation = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var safeReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        var elapsed = FormatElapsed(snapshot);
        var isComplete = snapshot.State is AppSurfaceDocsHarvestRunState.Completed or AppSurfaceDocsHarvestRunState.Failed;
        var stateLabel = snapshot.State == AppSurfaceDocsHarvestRunState.Failed ? "Needs attention" :
            snapshot.State == AppSurfaceDocsHarvestRunState.Completed ? "Ready" : "Assembling docs";
        var statusLine = snapshot.State switch
        {
            AppSurfaceDocsHarvestRunState.Completed => "Docs are ready. Taking you back to the page you asked for.",
            AppSurfaceDocsHarvestRunState.Failed => "Harvest finished with diagnostics. You can review recovery details below.",
            AppSurfaceDocsHarvestRunState.Running => "RazorWire is streaming harvest progress while AppSurface Docs builds the first snapshot.",
            _ => "Starting the first docs harvest."
        };

        var sb = new StringBuilder();
        sb.Append("<section class=\"docs-harvest-observatory\" aria-labelledby=\"docs-harvest-title\"");
        if (isComplete && snapshot.State == AppSurfaceDocsHarvestRunState.Completed)
        {
            sb.Append(" data-appsurface-docs-harvest-complete=\"true\"");
            sb.Append(" data-appsurface-docs-harvest-delay=\"").Append(completionDelayMilliseconds).Append('"');
            if (includeReturnNavigation)
            {
                sb.Append(" data-appsurface-docs-harvest-return-url=\"").Append(EncodeAttribute(safeReturnUrl)).Append('"');
            }
        }

        sb.Append('>');
        sb.Append("<header class=\"docs-harvest-header\">");
        sb.Append("<div class=\"docs-harvest-heading-group\">");
        sb.Append("<p class=\"docs-harvest-kicker\">AppSurface Docs</p>");
        sb.Append("<h1 id=\"docs-harvest-title\" class=\"docs-gradient-title docs-harvest-title\">").Append(Encode(stateLabel)).Append("</h1>");
        sb.Append("<p id=\"docs-harvest-status\" class=\"docs-harvest-status\" role=\"status\" aria-live=\"polite\">")
            .Append(Encode(statusLine))
            .Append("</p>");
        sb.Append("</div>");
        sb.Append(RenderRateAccent(snapshot));
        sb.Append("</header>");

        sb.Append("<div class=\"docs-harvest-metrics\" aria-label=\"Harvest metrics\">");
        AppendMetric(sb, "Elapsed", elapsed);
        AppendMetric(sb, "Docs processed", snapshot.TotalDocs.ToString(CultureInfo.InvariantCulture));
        AppendMetric(sb, "Harvesters", $"{snapshot.CompletedHarvesters}/{snapshot.TotalHarvesters}");
        sb.Append("</div>");

        sb.Append("<ol class=\"docs-harvest-phase-rail\" aria-label=\"Harvest phases\">");
        AppendPhase(sb, "Wake", "Done", true);
        AppendPhase(sb, "Scan", isComplete ? "Done" : "Now", snapshot.State == AppSurfaceDocsHarvestRunState.Running);
        AppendPhase(sb, "Index", isComplete ? "Done" : "Waiting", false);
        AppendPhase(sb, "Ready", isComplete ? "Done" : "Waiting", false);
        sb.Append("</ol>");

        sb.Append("<div class=\"docs-harvest-harvesters\" aria-label=\"Harvester progress\">");
        foreach (var harvester in snapshot.Harvesters)
        {
            sb.Append("<article class=\"docs-harvest-harvester\">");
            sb.Append("<span class=\"docs-harvest-harvester-name\">").Append(Encode(FriendlyHarvesterName(harvester.HarvesterType))).Append("</span>");
            sb.Append("<span class=\"docs-harvest-harvester-status\">").Append(Encode(harvester.Status)).Append("</span>");
            sb.Append("<span class=\"docs-harvest-harvester-count\">").Append(harvester.DocCount.ToString(CultureInfo.InvariantCulture)).Append(" docs</span>");
            sb.Append("</article>");
        }

        sb.Append("</div>");

        sb.Append("<section class=\"docs-harvest-activity\" aria-labelledby=\"docs-harvest-activity-heading\">");
        sb.Append("<h2 id=\"docs-harvest-activity-heading\">Live activity</h2>");
        sb.Append("<ul>");
        foreach (var activity in snapshot.Activity.Take(8))
        {
            sb.Append("<li><time datetime=\"").Append(EncodeAttribute(activity.TimestampUtc.ToString("O", CultureInfo.InvariantCulture))).Append("\">")
                .Append(Encode(FormatActivityTime(activity.TimestampUtc)))
                .Append("</time><span>")
                .Append(Encode(activity.Message))
                .Append("</span></li>");
        }

        sb.Append("</ul></section>");

        if (snapshot.Diagnostics.Count > 0)
        {
            sb.Append("<section class=\"docs-harvest-diagnostics\" aria-labelledby=\"docs-harvest-diagnostics-heading\">");
            sb.Append("<h2 id=\"docs-harvest-diagnostics-heading\">Recovery details</h2>");
            foreach (var diagnostic in snapshot.Diagnostics.Take(4))
            {
                sb.Append("<article>");
                sb.Append("<strong>").Append(Encode(diagnostic.Code)).Append("</strong>");
                sb.Append("<p>").Append(Encode(diagnostic.Problem)).Append("</p>");
                if (!string.IsNullOrWhiteSpace(diagnostic.Fix))
                {
                    sb.Append("<p>").Append(Encode(diagnostic.Fix)).Append("</p>");
                }

                sb.Append("</article>");
            }

            sb.Append("</section>");
        }

        if (isComplete && includeReturnNavigation)
        {
            sb.Append("<p class=\"docs-harvest-return-link\"><a href=\"")
                .Append(EncodeAttribute(safeReturnUrl))
                .Append("\">Continue to your docs page</a></p>");
        }

        sb.Append("</section>");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a Turbo stream update for the harvest observatory target.
    /// </summary>
    /// <param name="snapshot">The redacted snapshot to render.</param>
    /// <param name="completionDelayMilliseconds">The completion navigation delay in milliseconds.</param>
    /// <returns>A Turbo stream update that replaces the observatory content.</returns>
    /// <remarks>
    /// This wrapper renders with <c>returnUrl: "/"</c> and disables return navigation because the outer page owns
    /// navigation timing; the emitted fragment still carries completion state for client-side refresh scheduling.
    /// </remarks>
    internal static string RenderTurboStream(
        AppSurfaceDocsHarvestProgressSnapshot snapshot,
        int completionDelayMilliseconds)
    {
        return "<turbo-stream action=\"update\" target=\"docs-harvest-observatory\"><template>"
               + Render(snapshot, returnUrl: "/", completionDelayMilliseconds, includeReturnNavigation: false)
               + "</template></turbo-stream>";
    }

    private static void AppendMetric(StringBuilder sb, string label, string value)
    {
        sb.Append("<div class=\"docs-harvest-metric\"><span>")
            .Append(Encode(label))
            .Append("</span><strong>")
            .Append(Encode(value))
            .Append("</strong></div>");
    }

    private static void AppendPhase(StringBuilder sb, string label, string status, bool active)
    {
        sb.Append("<li");
        if (active)
        {
            sb.Append(" class=\"is-active\"");
        }

        sb.Append("><span>").Append(Encode(label)).Append("</span><strong>").Append(Encode(status)).Append("</strong></li>");
    }

    private static string RenderRateAccent(AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        return "<div class=\"docs-harvest-rate\" aria-label=\"Harvest speed\">"
               + "<span>Docs/sec</span>"
               + "<strong>" + Encode(FormatDocsPerSecond(snapshot)) + "</strong>"
               + "</div>";
    }

    private static string FormatDocsPerSecond(AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        var end = snapshot.CompletedUtc ?? DateTimeOffset.UtcNow;
        var elapsed = end - snapshot.StartedUtc;
        if (snapshot.TotalDocs <= 0 || elapsed <= TimeSpan.Zero)
        {
            return "0";
        }

        var rate = snapshot.TotalDocs / Math.Max(elapsed.TotalSeconds, 1);
        return rate switch
        {
            >= 100 => rate.ToString("0", CultureInfo.InvariantCulture),
            >= 10 => rate.ToString("0.0", CultureInfo.InvariantCulture),
            _ => rate.ToString("0.00", CultureInfo.InvariantCulture)
        };
    }

    private static string FriendlyHarvesterName(string harvesterType)
    {
        return harvesterType switch
        {
            nameof(MarkdownHarvester) => "Markdown",
            nameof(CSharpDocHarvester) => "C# API",
            nameof(JavaScriptDocHarvester) => "JavaScript public API",
            _ => harvesterType
        };
    }

    private static string FormatElapsed(AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        var end = snapshot.CompletedUtc ?? DateTimeOffset.UtcNow;
        var elapsed = end - snapshot.StartedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalSeconds < 60
            ? $"{Math.Max(1, (int)Math.Ceiling(elapsed.TotalSeconds))}s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
    }

    private static string FormatActivityTime(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - timestamp;
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return "now";
        }

        return elapsed.TotalMinutes < 1
            ? $"{(int)elapsed.TotalSeconds}s ago"
            : $"{(int)elapsed.TotalMinutes}m ago";
    }

    private static string Encode(string value)
    {
        return Encoder.Encode(value);
    }

    private static string EncodeAttribute(string value)
    {
        return Encoder.Encode(value);
    }
}
