using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using RazorWireWebExample.Models;

namespace RazorWireWebExample.Controllers;

/// <summary>
/// A version-bound MVC consumer fixture for Skoolit's V4 nested action/date editor.
/// </summary>
/// <remarks>
/// The source contract is pinned to Skoolit commit <c>a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e</c>,
/// <c>src/Skoolit.Web/Views/ForwardingProof/EvidenceCandidate.cshtml</c>, and the accompanying
/// <c>alpha-evidence-wizard.js</c>. This fixture adapts that field shape to ASP.NET Core's explicit-index
/// binder. Its bounded renderer adapter restores canonical posted marker keys in binder order before validation;
/// it does not infer ordinal keys or clear ModelState. It is not a live Skoolit patch and has no persistent data store.
/// </remarks>
public sealed class SkoolitV4CollectionController : Controller
{
    private const string UnsavedFeedback = "Unsaved date changes. Save labels to check the date and its year.";
    private const string InitialFeedback = "Enter the source month and day. The year is optional and should remain blank when the source omits it.";

    /// <summary>Shows two sparse action keys, each with its own shared sparse date keys.</summary>
    /// <param name="mode">The consumer removal policy: <c>physical</c> or <c>mark</c>.</param>
    /// <returns>The source-shaped date editor and its explicit-index templates.</returns>
    [HttpGet("/Reactivity/SkoolitV4Collections")]
    public IActionResult Index(string? mode = null)
    {
        return Editor(CreateFixtureModel(mode));
    }

    /// <summary>
    /// Accepts ordinary ASP.NET model binding, validates the pinned Discrete-end rule, or runs a V4 no-JavaScript command.
    /// </summary>
    /// <param name="model">The model-bound V4 action/date draft.</param>
    /// <param name="draftIntent">An ordinary-submit command used by the pinned view when JavaScript is unavailable.</param>
    /// <returns>A 422 with attempted values for invalid input, or the accepted draft.</returns>
    [HttpPost("/Reactivity/SkoolitV4Collections")]
    [ValidateAntiForgeryToken]
    public IActionResult Submit(SkoolitV4CollectionEditorModel model, [FromForm] string? draftIntent)
    {
        model.RemoveMode = model.RemoveMode == "mark" ? "mark" : "physical";
        if (!TryRestorePostedMarkerKeys(model))
        {
            return BadRequest("The posted collection markers cannot be represented by the bound action/date rows.");
        }

        RestoreSavedDates(model);
        if (!string.IsNullOrWhiteSpace(draftIntent))
        {
            if (!ApplyDraftIntent(model, draftIntent))
            {
                ModelState.AddModelError(nameof(draftIntent), "The requested draft command did not identify an available row.");
                Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return Editor(model);
            }

            RefreshTemporalFeedback(model);
            return Editor(model);
        }

        ValidateDraft(model);
        RefreshTemporalFeedback(model);
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }
        else
        {
            model.Result = "Form accepted for consumer persistence; this fixture has no data store.";
        }

        return Editor(model);
    }

    private bool TryRestorePostedMarkerKeys(SkoolitV4CollectionEditorModel model)
    {
        var actionKeys = Request.Form["Actions.index"].Select(value => (string?)value).ToArray();
        if (!AreCanonicalKeys(actionKeys, model.Actions.Count) || model.Actions.Any(action => action is null))
        {
            return false;
        }

        for (var actionPosition = 0; actionPosition < actionKeys.Length; actionPosition++)
        {
            var actionKey = actionKeys[actionPosition]!;
            var action = model.Actions[actionPosition];
            action.ClientIndex = actionKey;

            var dateKeys = Request.Form[$"Actions[{actionKey}].Dates.index"].Select(value => (string?)value).ToArray();
            if (!AreCanonicalKeys(dateKeys, action.Dates.Count) || action.Dates.Any(date => date is null))
            {
                return false;
            }

            for (var datePosition = 0; datePosition < dateKeys.Length; datePosition++)
            {
                var date = action.Dates[datePosition];
                date.ClientIndex = dateKeys[datePosition]!;
                date.ActionClientIndex = actionKey;
            }
        }

        return true;
    }

    private static bool AreCanonicalKeys(IReadOnlyList<string?> keys, int boundCount)
    {
        if (keys.Count != boundCount)
        {
            return false;
        }

        // Form field names and ModelState paths compare without case. Preserve accepted key spelling.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateDraft(SkoolitV4CollectionEditorModel model)
    {
        for (var actionPosition = 0; actionPosition < model.Actions.Count; actionPosition++)
        {
            var action = model.Actions[actionPosition];
            if (action.Delete)
            {
                continue;
            }

            var actionPrefix = $"Actions[{action.ClientIndex}]";
            for (var datePosition = 0; datePosition < action.Dates.Count; datePosition++)
            {
                var date = action.Dates[datePosition];
                if (date.Delete)
                {
                    continue;
                }

                var datePrefix = $"{actionPrefix}.Dates[{date.ClientIndex}]";
                if (!string.IsNullOrWhiteSpace(date.Shape) && date.Shape is not ("Discrete" or "Range"))
                {
                    ModelState.AddModelError($"{datePrefix}.Shape", "Choose Discrete or Range.");
                }
                if (date.Shape == "Discrete")
                {
                    AddDiscreteEndError(date.EndMonth, $"{datePrefix}.EndMonth");
                    AddDiscreteEndError(date.EndDay, $"{datePrefix}.EndDay");
                    AddDiscreteEndError(date.EndYear, $"{datePrefix}.EndYear");
                }
            }
        }
    }

    private void AddDiscreteEndError(string? value, string field)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ModelState.AddModelError(field, "End date parts apply only to a Range; the submitted value is retained for correction.");
        }
    }

    private bool ApplyDraftIntent(SkoolitV4CollectionEditorModel model, string intent)
    {
        var parts = intent.Split('|');
        if (parts.Length == 1 && parts[0] == "add-action")
        {
            model.Actions.Add(NewAction(UniqueKey(model.Actions.Select(action => action.ClientIndex))));
            return true;
        }

        if (parts.Length == 2 && parts[0] == "add-date" && TryPosition(parts[1], model.Actions.Count, out var addActionPosition))
        {
            var action = model.Actions[addActionPosition];
            if (action.Delete)
            {
                return false;
            }

            action.Dates.Add(NewDate(UniqueKey(action.Dates.Select(date => date.ClientIndex)), action.ClientIndex));
            return true;
        }

        if (parts.Length == 2 && parts[0] == "remove-action" && TryPosition(parts[1], model.Actions.Count, out var removeActionPosition))
        {
            if (model.RemoveMode == "mark")
            {
                model.Actions[removeActionPosition].Delete = true;
            }
            else
            {
                model.Actions.RemoveAt(removeActionPosition);
            }

            return true;
        }

        if (parts.Length == 2 && parts[0] == "duplicate-action" && TryPosition(parts[1], model.Actions.Count, out var duplicateActionPosition))
        {
            var source = model.Actions[duplicateActionPosition];
            if (source.Delete)
            {
                return false;
            }

            var duplicateKey = UniqueKey(model.Actions.Select(action => action.ClientIndex));
            var duplicate = new SkoolitV4CollectionActionModel
            {
                ClientIndex = duplicateKey,
                Title = source.Title,
                Dates = CloneLiveDates(source.Dates, duplicateKey)
            };
            model.Actions.Insert(duplicateActionPosition + 1, duplicate);
            return true;
        }

        if (parts.Length == 3
            && parts[0] == "remove-date"
            && TryPosition(parts[1], model.Actions.Count, out var ownerPosition)
            && TryPosition(parts[2], model.Actions[ownerPosition].Dates.Count, out var datePosition))
        {
            var owner = model.Actions[ownerPosition];
            if (owner.Delete)
            {
                return false;
            }

            if (model.RemoveMode == "mark")
            {
                owner.Dates[datePosition].Delete = true;
            }
            else
            {
                owner.Dates.RemoveAt(datePosition);
            }

            return true;
        }

        return false;
    }

    private void RestoreSavedDates(SkoolitV4CollectionEditorModel model)
    {
        foreach (var date in model.Actions.SelectMany(action => action.Dates))
        {
            date.SavedDate = date.Id is null ? null : SavedFixtureDates.GetValueOrDefault(date.Id);
        }
    }

    private void RefreshTemporalFeedback(SkoolitV4CollectionEditorModel model)
    {
        foreach (var date in model.Actions.SelectMany(action => action.Dates))
        {
            date.TemporalFeedback = date.SavedDate is not null && MatchesSaved(date, date.SavedDate)
                ? SavedResult(date.SavedDate)
                : date.SavedDate is not null || HasDraftDateParts(date) || HasDateError(date)
                    ? UnsavedFeedback
                    : InitialFeedback;
        }
    }

    // Identity-free populated dates are unsaved drafts across requests; an empty newly added date keeps its initial prompt.
    private static bool HasDraftDateParts(SkoolitV4CollectionDateModel date) => new[]
    {
        date.Kind, date.Shape, date.StartMonth, date.StartDay, date.StartYear,
        date.EndMonth, date.EndDay, date.EndYear, date.StartTime, date.EndTime, date.TimeZone
    }.Any(value => !string.IsNullOrWhiteSpace(value));

    private bool HasDateError(SkoolitV4CollectionDateModel date)
    {
        var prefix = $"Actions[{date.ActionClientIndex}].Dates[{date.ClientIndex}]";
        return ModelState.Any(entry => (entry.Value?.Errors.Count ?? 0) > 0
            && entry.Key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSaved(SkoolitV4CollectionDateModel draft, SkoolitV4CollectionSavedDate saved)
    {
        return Same(draft.Kind, saved.Kind)
            && Same(draft.Shape, saved.Shape)
            && Same(draft.StartMonth, saved.StartMonth)
            && Same(draft.StartDay, saved.StartDay)
            && Same(draft.StartYear, saved.StartYear)
            && Same(draft.EndMonth, saved.EndMonth)
            && Same(draft.EndDay, saved.EndDay)
            && Same(draft.EndYear, saved.EndYear)
            && Same(draft.StartTime, saved.StartTime)
            && Same(draft.EndTime, saved.EndTime)
            && Same(draft.TimeZone, saved.TimeZone);
    }

    private static bool Same(string? left, string? right)
    {
        return string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string SavedResult(SkoolitV4CollectionSavedDate saved)
    {
        var start = SourceDate(saved.StartMonth, saved.StartDay, saved.StartYear);
        var sourceYear = string.IsNullOrWhiteSpace(saved.StartYear) ? "; no source year stated" : string.Empty;
        var end = string.IsNullOrWhiteSpace(saved.EndMonth) || string.IsNullOrWhiteSpace(saved.EndDay)
            ? string.Empty
            : $" through {SourceDate(saved.EndMonth, saved.EndDay, saved.EndYear)}";
        var time = string.IsNullOrWhiteSpace(saved.StartTime) ? string.Empty : $" at {saved.StartTime}";
        var timeZone = string.IsNullOrWhiteSpace(saved.TimeZone) ? string.Empty : $" ({saved.TimeZone})";
        return $"Last saved result: {start}{end}{sourceYear}{time}{timeZone}.";
    }

    private static string SourceDate(string? month, string? day, string? year)
    {
        var monthName = int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 12
            ? CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(number)
            : month ?? string.Empty;
        return string.IsNullOrWhiteSpace(year) ? $"{monthName} {day}" : $"{monthName} {day}, {year}";
    }

    private static bool TryPosition(string value, int count, out int position)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out position)
            && position >= 0
            && position < count;
    }

    private static string UniqueKey(IEnumerable<string> existing)
    {
        var keys = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate;
        do
        {
            candidate = $"draft-{Guid.NewGuid():N}";
        }
        while (keys.Contains(candidate));

        return candidate;
    }

    private static SkoolitV4CollectionActionModel NewAction(string key)
    {
        return new SkoolitV4CollectionActionModel
        {
            ClientIndex = key,
            Dates = [NewDate("0", key)]
        };
    }

    private static SkoolitV4CollectionDateModel NewDate(string key, string actionKey)
    {
        return new SkoolitV4CollectionDateModel { ClientIndex = key, ActionClientIndex = actionKey };
    }

    private static SkoolitV4CollectionDateModel CloneDate(SkoolitV4CollectionDateModel source, string key, string actionKey)
    {
        return new SkoolitV4CollectionDateModel
        {
            ClientIndex = key,
            ActionClientIndex = actionKey,
            Kind = source.Kind,
            Shape = source.Shape,
            StartMonth = source.StartMonth,
            StartDay = source.StartDay,
            StartYear = source.StartYear,
            EndMonth = source.EndMonth,
            EndDay = source.EndDay,
            EndYear = source.EndYear,
            StartTime = source.StartTime,
            EndTime = source.EndTime,
            TimeZone = source.TimeZone,
            TemporalFeedback = UnsavedFeedback
        };
    }

    private static List<SkoolitV4CollectionDateModel> CloneLiveDates(
        IEnumerable<SkoolitV4CollectionDateModel> sourceDates,
        string actionKey)
    {
        var dates = sourceDates.Where(date => !date.Delete).ToArray();
        var reservedKeys = sourceDates.Select(date => date.ClientIndex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clones = new List<SkoolitV4CollectionDateModel>(dates.Length);
        foreach (var date in dates)
        {
            var key = UniqueKey(reservedKeys);
            reservedKeys.Add(key);
            clones.Add(CloneDate(date, key, actionKey));
        }

        return clones;
    }

    private static SkoolitV4CollectionEditorModel CreateFixtureModel(string? mode)
    {
        var model = new SkoolitV4CollectionEditorModel { RemoveMode = mode == "mark" ? "mark" : "physical" };
        model.Actions.Add(new SkoolitV4CollectionActionModel
        {
            ClientIndex = "4",
            Id = "action-4",
            RowVersion = "row-version-4",
            Title = "Field day",
            Dates =
            [
                SavedDate("0", "date-4-0", "date-concurrency-4-0", "eventStart", "Discrete", "6", "12", "", "", "", "", "09:00", "", "America/New_York"),
                SavedDate("7", "date-4-7", "date-concurrency-4-7", "dueBy", "Range", "6", "12", "", "6", "14", "", "09:00", "17:00", "America/New_York")
            ]
        });
        model.Actions.Add(new SkoolitV4CollectionActionModel
        {
            ClientIndex = "9",
            Id = "action-9",
            RowVersion = "row-version-9",
            Title = "Submit registration",
            Dates =
            [
                SavedDate("0", "date-9-0", "date-concurrency-9-0", "overdueSince", "Discrete", "9", "30", "2026", "", "", "", "", "", "America/New_York"),
                SavedDate("7", "date-9-7", "date-concurrency-9-7", "dueBy", "Range", "10", "1", "", "10", "3", "", "", "", "America/New_York")
            ]
        });
        foreach (var action in model.Actions)
        {
            foreach (var date in action.Dates)
            {
                date.ActionClientIndex = action.ClientIndex;
            }
        }

        return model;
    }

    private static SkoolitV4CollectionDateModel SavedDate(
        string key,
        string id,
        string concurrencyToken,
        string kind,
        string shape,
        string startMonth,
        string startDay,
        string startYear,
        string endMonth,
        string endDay,
        string endYear,
        string startTime,
        string endTime,
        string timeZone)
    {
        var date = new SkoolitV4CollectionDateModel
        {
            ClientIndex = key,
            Id = id,
            ConcurrencyToken = concurrencyToken,
            Kind = kind,
            Shape = shape,
            StartMonth = startMonth,
            StartDay = startDay,
            StartYear = startYear,
            EndMonth = endMonth,
            EndDay = endDay,
            EndYear = endYear,
            StartTime = startTime,
            EndTime = endTime,
            TimeZone = timeZone
        };
        date.SavedDate = ToSavedDate(date);
        date.TemporalFeedback = SavedResult(date.SavedDate);
        return date;
    }

    private static SkoolitV4CollectionSavedDate ToSavedDate(SkoolitV4CollectionDateModel date)
    {
        return new SkoolitV4CollectionSavedDate(
            date.Kind,
            date.Shape,
            date.StartMonth,
            date.StartDay,
            date.StartYear,
            date.EndMonth,
            date.EndDay,
            date.EndYear,
            date.StartTime,
            date.EndTime,
            date.TimeZone);
    }

    private static readonly Dictionary<string, SkoolitV4CollectionSavedDate> SavedFixtureDates =
        CreateFixtureModel("physical").Actions
            .SelectMany(action => action.Dates)
            .Where(date => date.Id is not null && date.SavedDate is not null)
            .ToDictionary(date => date.Id!, date => date.SavedDate!, StringComparer.Ordinal);

    private ViewResult Editor(SkoolitV4CollectionEditorModel model)
    {
        ViewData["RemoveMode"] = model.RemoveMode;
        return View("~/Views/Reactivity/SkoolitV4CollectionEditor.cshtml", model);
    }
}
