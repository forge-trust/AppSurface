using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Mvc;
using RazorWireWebExample.Models;
using RazorWireWebExample.Services;
using RazorWireWebExample.ViewComponents;

namespace RazorWireWebExample.Controllers;

/// <summary>
/// A controller that facilitates real-time reactivity, including user registration, chat messaging, and counter synchronization.
/// </summary>
public class ReactivityController : Controller
{
    private readonly IRazorWireStreamHub _hub;
    private readonly IUserPresenceService _presence;
    private readonly IMessageStore _messages;

    /// <summary>
    /// Initializes a new instance of <see cref="ReactivityController"/> with the specified stream hub, user presence service, and message store.
    /// </summary>
    public ReactivityController(IRazorWireStreamHub hub, IUserPresenceService presence, IMessageStore messages)
    {
        _hub = hub;
        _presence = presence;
        _messages = messages;
    }

    /// <summary>
    /// Renders the controller's default view for the reactivity UI.
    /// </summary>
    /// <returns>An <see cref="IActionResult"/> that renders the default view.</returns>
    public IActionResult Index()
    {
        return View(_messages.GetAll());
    }

    /// <summary>
    /// Renders the RazorWire failed-form UX demonstration page.
    /// </summary>
    /// <returns>An <see cref="IActionResult"/> that renders the failed-form UX sample.</returns>
    public IActionResult FormFailures()
    {
        return View();
    }

    /// <summary>
    /// Renders the sidebar inside a RazorWire frame with the ID "permanent-island".
    /// </summary>
    /// <returns>An <see cref="IActionResult"/> that produces the RazorWire frame containing the sidebar content.</returns>
    public IActionResult Sidebar()
    {
        return RazorWireBridge.Frame(this, "permanent-island", "_Sidebar");
    }

    /// <summary>
    /// Renders the UserList view component inside a Turbo/RazorWire frame with the ID "user-list".
    /// </summary>
    /// <returns>An <see cref="IActionResult"/> that renders the UserList view component wrapped in a Turbo/RazorWire frame with id "user-list".</returns>
    public IActionResult UserList()
    {
        // Wrap the ViewComponent in a Turbo Frame for the island
        return RazorWireBridge.FrameComponent(this, "user-list", "UserList");
    }

    /// <summary>
    /// Registers the provided username, persists it in an HttpOnly cookie, and broadcasts the user's presence to other clients.
    /// </summary>
    /// <param name="username">The username submitted from the registration form. If empty or whitespace, no username is persisted or broadcast.</param>
    /// <returns>A Turbo Stream that replaces the message form and updates the register form when the request is a Turbo request; otherwise a redirect to the Index action.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterUser([FromForm] string username)
    {
        var trimmedUsername = string.Empty;

        if (!string.IsNullOrWhiteSpace(username))
        {
            trimmedUsername = username.Trim();

            // Set cookie to persist username
            Response.Cookies.Append(
                "razorwire-username",
                trimmedUsername,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });

            // Broadcast update to everyone
            await BroadcastUserPresenceAsync(trimmedUsername);
        }

        if (Request.IsTurboRequest())
        {
            return this.RazorWireStream()
                .ReplacePartial("message-form", "_MessageForm", trimmedUsername)
                .UpdatePartial("register-form", "_RegisterForm")
                .BuildResult();
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Publishes a chat message to connected clients and returns a Turbo/RazorWire stream or a redirect.
    /// </summary>
    /// <param name="message">The message text submitted from the form.</param>
    /// <returns>`A Turbo/RazorWire` stream that replaces the message form when the request is a Turbo request; otherwise a redirect to the Index action.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishMessage([FromForm] string message)
    {
        var effectiveUsername = Request.Cookies["razorwire-username"];
        var isAnonymous = string.IsNullOrWhiteSpace(effectiveUsername);
        var displayName = isAnonymous ? "Anonymous" : effectiveUsername!.Trim();

        if (!isAnonymous)
        {
            await BroadcastUserPresenceAsync(displayName);
        }

        var utcTime = DateTimeOffset.UtcNow.ToString("o");
        var item = new MessageItemModel(displayName, utcTime, message);
        _messages.Add(item);
        var viewContext = this.CreateViewContext();
        var streamHtml = await RazorWireBridge.CreateStream()
            .Remove("messages-empty")
            .PrependPartial("messages", "_MessageItem", item)
            .RenderAsync(viewContext);

        await _hub.PublishAsync("reactivity", streamHtml);

        // 2. Return stream result to caller
        if (Request.IsTurboRequest())
        {
            return this.RazorWireStream()
                .ReplacePartial("message-form", "_MessageForm", isAnonymous ? "" : displayName)
                .BuildResult();
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Increments the server and session counters and returns a Turbo/RazorWire stream to update the UI or a safe redirect.
    /// </summary>
    /// <param name="clientCount">The current session client count (will be incremented).</param>
    /// <returns>`IActionResult` that is a Turbo stream updating counters for Turbo requests; otherwise a redirect to the referring local URL or the Index action.</returns>
    // docs:snippet razorwire-increment-counter:start
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult IncrementCounter([FromForm] int clientCount)
    {
        CounterViewComponent.Increment();
        clientCount++;

        if (Request.IsTurboRequest())
        {
            return this.RazorWireStream()
                .Update(
                    "instance-score-value",
                    CounterViewComponent.Count.ToString())
                .Update("session-score-value", clientCount.ToString())
                .ReplacePartial(
                    "client-count-input",
                    "_CounterInput",
                    clientCount)
                .BuildResult();
        }

        // Safe redirect
        var referer = Request.Headers["Referer"].ToString();

        return Url.IsLocalUrl(referer) ? Redirect(referer) : RedirectToAction(nameof(Index));
    }
    // docs:snippet razorwire-increment-counter:end

    /// <summary>
    /// Demonstrates a server-handled validation failure that returns a form-local RazorWire error summary.
    /// </summary>
    /// <param name="displayName">The display name submitted by the sample form.</param>
    /// <returns>A Turbo Stream validation response for enhanced requests, or the demo page for fallback requests.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult SubmitValidationFailure([FromForm] string? displayName)
    {
        var normalizedDisplayName = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDisplayName))
        {
            ModelState.AddModelError(nameof(displayName), "Display name is required.");
        }

        if (normalizedDisplayName is { Length: > 20 })
        {
            ModelState.AddModelError(nameof(displayName), "Display name must be 20 characters or fewer.");
        }

        if (!ModelState.IsValid)
        {
            if (Request.IsTurboRequest())
            {
                return this.RazorWireStream()
                    .FormValidationErrors("validation-errors", ModelState, maxErrors: 4)
                    .BuildResult(StatusCodes.Status422UnprocessableEntity);
            }

            return View(nameof(FormFailures));
        }

        if (Request.IsTurboRequest())
        {
            var savedDisplayName = normalizedDisplayName!;

            return this.RazorWireStream()
                .Update(
                    "validation-result",
                    $"""
                    <p class="text-sm font-semibold text-emerald-700">Saved {System.Net.WebUtility.HtmlEncode(savedDisplayName)}.</p>
                    """)
                .Update("validation-errors", string.Empty)
                .BuildResult();
        }

        return RedirectToAction(nameof(FormFailures));
    }

    /// <summary>
    /// Endpoint used by the sample page to demonstrate RazorWire's development anti-forgery diagnostics.
    /// </summary>
    /// <returns>An OK result when anti-forgery succeeds; the action body is not reached by the intentional sample failure.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult AntiforgeryFailureDemo()
    {
        return Ok();
    }

    /// <summary>
    /// Demonstrates the default form-local fallback for authorization-style failures.
    /// </summary>
    /// <returns>A 403 response without a server-handled RazorWire form error.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult AuthorizationFailureDemo()
    {
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Demonstrates the default form-local fallback for malformed request failures.
    /// </summary>
    /// <returns>A 400 response without a server-handled RazorWire form error.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult MalformedFailureDemo()
    {
        return BadRequest();
    }

    /// <summary>
    /// Demonstrates the default form-local fallback for unexpected server failures.
    /// </summary>
    /// <returns>A 500 response without a server-handled RazorWire form error.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ServerFailureDemo()
    {
        return StatusCode(StatusCodes.Status500InternalServerError);
    }

    /// <summary>
    /// Broadcasts a user's presence to connected clients and updates the rendered user list and count.
    /// </summary>
    /// <param name="username">The display name to mark as active and include in the rendered user list.</param>
    private async Task BroadcastUserPresenceAsync(string username)
    {
        // Record activity and get the current active count
        var activeCount = _presence.RecordActivity(username);

        var viewContext = this.CreateViewContext();
        var streamHtml = await RazorWireBridge.CreateStream()
            .Remove("user-list-empty")
            .AppendPartial(
                "user-list-items",
                "Components/UserList/_UserItem",
                new UserPresenceInfo(
                    username,
                    StringUtils.ToSafeId(username, appendHash: true),
                    DateTimeOffset.UtcNow))
            .Update("user-count", $"{activeCount} ONLINE")
            .RenderAsync(viewContext);

        await _hub.PublishAsync("reactivity", streamHtml);
    }
}
