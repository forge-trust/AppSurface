using Microsoft.AspNetCore.Mvc;
using RazorWireWebExample.Models;

namespace RazorWireWebExample.Controllers;

/// <summary>Runnable consumer proof for sparse nested MVC binding and invalid redisplay.</summary>
/// <remarks>This is a bounded equivalent of Skoolit's V4 action/date shape, not live downstream adoption. Rendering keys come from canonical posted markers, not list ordinals or ClientIndex fields.</remarks>
public sealed class NestedCollectionsController : Controller
{
    /// <summary>Shows two actions with shared sparse date keys.</summary>
    /// <param name="mode">Physical or mark removal.</param>
    /// <returns>The nested collection editor.</returns>
    [HttpGet("Reactivity/NestedCollections")]
    public IActionResult Index(string mode = "physical")
    {
        var model = new NestedCollectionsSampleModel { RemoveMode = mode == "mark" ? "mark" : "physical" };
        foreach (var key in new[] { "4", "9" })
        {
            var action = new NestedActionModel { ClientIndex = key, Id = $"action-{key}", RowVersion = $"version-{key}", Title = $"Action {key}" };
            foreach (var dateKey in new[] { "0", "7" })
            {
                action.Dates.Add(new NestedDateModel { ClientIndex = dateKey, Id = $"date-{key}-{dateKey}", ConcurrencyToken = "original", Kind = "Scheduled", Date = new DateOnly(2026, 10, 14) });
            }
            model.Actions.Add(action);
        }
        return Editor(model);
    }

    /// <summary>Binds nested rows, preserves posted keys and attempted values, and applies consumer validation.</summary>
    /// <param name="model">The nested bound model.</param>
    /// <param name="addDateFor">An optional no-JavaScript command identifying a parent.</param>
    /// <param name="addAction">An optional no-JavaScript add command.</param>
    /// <returns>A validation redisplay or the accepted rows with their original keys.</returns>
    [HttpPost("Reactivity/NestedCollections")]
    [ValidateAntiForgeryToken]
    public IActionResult Submit(NestedCollectionsSampleModel model, string? addDateFor, bool addAction = false)
    {
        model.RemoveMode = model.RemoveMode == "mark" ? "mark" : "physical";
        var keys = Request.Form["Actions.index"].ToArray();
        if (!ValidKeys(keys, model.Actions.Count) || model.Actions.Any(action => action is null)) return BadRequest("Actions.index must contain unique, nonblank keys matching bound rows.");
        for (var i = 0; i < keys.Length; i++)
        {
            var action = model.Actions[i];
            action.ClientIndex = keys[i]!;
            var prefix = $"Actions[{action.ClientIndex}]";
            var dateKeys = Request.Form[$"{prefix}.Dates.index"].ToArray();
            if (!ValidKeys(dateKeys, action.Dates.Count) || action.Dates.Any(date => date is null)) return BadRequest("Date markers must contain unique, nonblank keys matching bound rows.");
            for (var j = 0; j < dateKeys.Length; j++) action.Dates[j].ClientIndex = dateKeys[j]!;
            if (action.Delete) continue;
            if (string.IsNullOrWhiteSpace(action.Title)) ModelState.AddModelError($"{prefix}.Title", "Enter an action title.");
            if (action.Dates.Count(date => !date.Delete) > 8) ModelState.AddModelError($"{prefix}.Dates", "Use at most eight dates per action.");
            foreach (var date in action.Dates.Where(date => !date.Delete))
            {
                var datePrefix = $"{prefix}.Dates[{date.ClientIndex}]";
                if (string.IsNullOrWhiteSpace(date.Kind)) ModelState.AddModelError($"{datePrefix}.Kind", "Choose a date kind.");
                if (date.Date is null && (!ModelState.TryGetValue($"{datePrefix}.Date", out var entry) || entry.Errors.Count == 0)) ModelState.AddModelError($"{datePrefix}.Date", "Enter a date.");
            }
        }
        if (addAction || addDateFor is not null)
        {
            if (addAction) model.Actions.Add(new NestedActionModel { ClientIndex = Guid.NewGuid().ToString("N") });
            else
            {
                var owner = model.Actions.SingleOrDefault(action => action.ClientIndex == addDateFor && !action.Delete);
                if (owner is not null && owner.Dates.Count(date => !date.Delete) < 8) owner.Dates.Add(new NestedDateModel { ClientIndex = Guid.NewGuid().ToString("N") });
            }
            return Editor(model);
        }
        if (!ModelState.IsValid) Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        else model.Result = $"Saved {model.Actions.Count(action => !action.Delete)} action row(s).";
        return Editor(model);
    }

    // This adapter intentionally does not infer a universal binder key grammar.
    // It rejects failed/ambiguous binding rather than rekeying or clearing ModelState.
    private static bool ValidKeys(string?[] keys, int boundCount) => keys.Length == boundCount
        && keys.All(key => !string.IsNullOrWhiteSpace(key)) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Length;

    private ViewResult Editor(NestedCollectionsSampleModel model) => View("~/Views/Reactivity/NestedCollections.cshtml", model);
}
