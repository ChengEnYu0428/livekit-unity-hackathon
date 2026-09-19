using System;
using System.Collections.Generic;
using System.Globalization;
using Jorjin.Streaming;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Google Calendar–style month view of the meeting's action items: a 6×7 month
/// grid with today circled and coloured event chips per owner, plus a side panel
/// with the selected day's tasks and the tasks that have no date yet.
/// </summary>
public sealed class LiveKitCalendarView
{
    // Google Calendar dark theme.
    private static readonly Color32 Background = new(31, 31, 31, 255);
    private static readonly Color32 GridLine = new(60, 64, 67, 255);
    private static readonly Color32 Cell = new(31, 31, 31, 255);
    private static readonly Color32 CellOtherMonth = new(26, 26, 26, 255);
    private static readonly Color32 CellSelected = new(40, 52, 70, 255);
    private static readonly Color32 TextPrimary = new(227, 227, 227, 255);
    private static readonly Color32 TextMuted = new(154, 160, 166, 255);
    private static readonly Color32 TextFaint = new(95, 99, 104, 255);
    private static readonly Color32 TodayFill = new(168, 199, 250, 255);
    private static readonly Color32 TodayText = new(6, 46, 111, 255);
    private static readonly Color32 Sidebar = new(40, 42, 45, 255);
    private static readonly Color32 Card = new(53, 54, 58, 255);
    // Google Calendar event colours: Peacock, Tomato, Basil, Grape, Tangerine, Blueberry, Flamingo, Sage, Banana, Lavender.
    private static readonly Color32[] Palette =
    {
        new(3, 155, 229, 255), new(213, 0, 0, 255), new(11, 128, 67, 255), new(142, 36, 170, 255),
        new(244, 81, 30, 255), new(63, 81, 181, 255), new(230, 124, 115, 255), new(51, 182, 121, 255),
        new(246, 191, 38, 255), new(121, 134, 203, 255)
    };
    private static readonly string[] WeekdayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private const int ChipsPerCell = 2;

    private readonly Font font;
    private readonly GameObject root;
    private readonly Text monthLabel, summaryLabel, dayTitle;
    private readonly DayCell[] cells = new DayCell[42];
    private readonly RectTransform sidebarContent, gridArea, side;
    private bool portrait;
    private readonly List<CollaborationTask> tasks = new();
    private DateTime month, selected;

    private sealed class DayCell
    {
        public Image Background, Circle;
        public Text Number, More;
        public RectTransform Chips;
        public DateTime Date;
    }

    public bool Visible => root.activeSelf;

    public LiveKitCalendarView(Transform parent, Font font, Action close)
    {
        this.font = font;
        root = Rect("Calendar", parent).gameObject;
        Stretch((RectTransform)root.transform, Vector2.zero, Vector2.one, new Vector2(20, 65), new Vector2(-20, -109));
        Fill(root, Background, true);

        // Top bar: Back  Today  ‹ ›  September 2026                     N items
        RectTransform bar = Rect("Top bar", root.transform);
        Stretch(bar, new Vector2(0, 1), Vector2.one, new Vector2(0, -58), Vector2.zero);
        AddButton(bar, "Back", new Vector2(14, -11), new Vector2(76, 36), Background, true, close);
        AddButton(bar, "Today", new Vector2(102, -11), new Vector2(76, 36), Background, true, () => { selected = DateTime.Today; month = FirstOfMonth(selected); Render(); });
        AddButton(bar, "‹", new Vector2(188, -11), new Vector2(36, 36), Background, true, () => { month = month.AddMonths(-1); Render(); });
        AddButton(bar, "›", new Vector2(228, -11), new Vector2(36, 36), Background, true, () => { month = month.AddMonths(1); Render(); });
        monthLabel = Label(bar, "", 26, TextPrimary, TextAnchor.MiddleLeft);
        Place(monthLabel.rectTransform, new Vector2(280, -8), new Vector2(360, 42));
        summaryLabel = Label(bar, "", 17, TextMuted, TextAnchor.MiddleRight);
        Stretch(summaryLabel.rectTransform, new Vector2(.5f, 0), Vector2.one, Vector2.zero, new Vector2(-18, 0));

        // Month grid (left 70%): weekday header + 6 rows × 7 columns.
        gridArea = Rect("Month", root.transform);
        Stretch(gridArea, Vector2.zero, new Vector2(.70f, 1), new Vector2(12, 12), new Vector2(-6, -62));
        RectTransform header = Rect("Weekdays", gridArea);
        Stretch(header, new Vector2(0, 1), Vector2.one, new Vector2(0, -28), Vector2.zero);
        for (int d = 0; d < 7; d++)
        {
            Text name = Label(header, WeekdayNames[d], 14, TextMuted, TextAnchor.MiddleCenter);
            Stretch(name.rectTransform, new Vector2(d / 7f, 0), new Vector2((d + 1) / 7f, 1), Vector2.zero, Vector2.zero);
        }
        RectTransform body = Rect("Days", gridArea);
        Stretch(body, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0, -30));
        Fill(body.gameObject, GridLine, false);
        for (int i = 0; i < cells.Length; i++) cells[i] = CreateCell(body, i);

        // Side panel (right 30%): selected day + undated tasks.
        side = Rect("Agenda", root.transform);
        Stretch(side, new Vector2(.70f, 0), Vector2.one, new Vector2(6, 12), new Vector2(-12, -62));
        LiveKitMeetingStyle.ApplyRounded(side.gameObject.AddComponent<Image>(), Sidebar);
        dayTitle = Label(side, "", 21, TextPrimary, TextAnchor.MiddleLeft);
        Stretch(dayTitle.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(16, -50), new Vector2(-12, -8));
        sidebarContent = ScrollList(side);

        month = FirstOfMonth(DateTime.Today);
        selected = DateTime.Today;
        root.SetActive(false);
    }

    public void Show(CollaborationResult result, bool jumpToEvents = false)
    {
        Refresh(result);
        if (jumpToEvents) JumpToFirstEvent();
        root.SetActive(true);
        root.transform.SetAsLastSibling();
    }

    /// <summary>Refreshes the data; the visible month and selected day are kept.</summary>
    public void Refresh(CollaborationResult result)
    {
        tasks.Clear();
        if (result?.tasks != null)
            foreach (CollaborationTask task in result.tasks) if (task != null) tasks.Add(task);
        Render();
    }

    public void Hide() => root.SetActive(false);

    /// <summary>Selects the first upcoming dated item (or the first one) so new events are in view.</summary>
    private void JumpToFirstEvent()
    {
        DateTime? first = null, upcoming = null;
        foreach (CollaborationTask task in tasks)
        {
            if (!TryDate(task, out DateTime date)) continue;
            if (first == null || date < first) first = date;
            if (date >= DateTime.Today && (upcoming == null || date < upcoming)) upcoming = date;
        }
        DateTime? target = upcoming ?? first;
        if (target == null) return;
        selected = target.Value;
        month = FirstOfMonth(selected);
        Render();
    }

    private static string TimeRange(CollaborationTask task) =>
        string.IsNullOrEmpty(task.start_time) ? "" :
        string.IsNullOrEmpty(task.end_time) ? task.start_time : task.start_time + "–" + task.end_time;

    /// <summary>Side by side in landscape; month above the day list on an upright phone.</summary>
    public void SetPortrait(bool value)
    {
        if (value == portrait && gridArea.anchorMax != Vector2.zero) return;
        portrait = value;
        if (portrait)
        {
            Stretch(gridArea, new Vector2(0, .40f), Vector2.one, new Vector2(12, 6), new Vector2(-12, -62));
            Stretch(side, Vector2.zero, new Vector2(1, .40f), new Vector2(12, 12), new Vector2(-12, -6));
        }
        else
        {
            Stretch(gridArea, Vector2.zero, new Vector2(.70f, 1), new Vector2(12, 12), new Vector2(-6, -62));
            Stretch(side, new Vector2(.70f, 0), Vector2.one, new Vector2(6, 12), new Vector2(-12, -62));
        }
    }

    private void Render()
    {
        monthLabel.text = month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        int dated = 0;
        foreach (CollaborationTask task in tasks) if (TryDate(task, out _)) dated++;
        summaryLabel.text = tasks.Count == 0 ? "No action items yet" : $"{tasks.Count} items · {dated} on the calendar";

        DateTime start = month.AddDays(-(int)month.DayOfWeek);
        for (int i = 0; i < cells.Length; i++)
        {
            DayCell cell = cells[i];
            cell.Date = start.AddDays(i);
            bool inMonth = cell.Date.Month == month.Month;
            bool today = cell.Date == DateTime.Today;
            cell.Background.color = cell.Date == selected ? CellSelected : inMonth ? Cell : CellOtherMonth;
            cell.Number.text = cell.Date.Day == 1 ? cell.Date.ToString("MMM d", CultureInfo.InvariantCulture) : cell.Date.Day.ToString();
            cell.Number.color = today ? TodayText : inMonth ? TextPrimary : TextFaint;
            cell.Circle.enabled = today;
            cell.Circle.rectTransform.sizeDelta = new Vector2(cell.Date.Day == 1 ? 64 : 28, 28);

            foreach (Transform child in cell.Chips) UnityEngine.Object.Destroy(child.gameObject);
            List<CollaborationTask> day = TasksOn(cell.Date);
            for (int c = 0; c < Math.Min(ChipsPerCell, day.Count); c++) Chip(cell.Chips, day[c], c);
            cell.More.text = day.Count > ChipsPerCell ? $"{day.Count - ChipsPerCell} more" : "";
        }
        RenderAgenda();
    }

    private void RenderAgenda()
    {
        dayTitle.text = selected.ToString("dddd, MMM d", CultureInfo.InvariantCulture);
        foreach (Transform child in sidebarContent) UnityEngine.Object.Destroy(child.gameObject);

        List<CollaborationTask> day = TasksOn(selected);
        if (day.Count == 0) Note(tasks.Count == 0 ? "No action items yet. Type something like \"Amy does the slides by Wednesday\" and press Action Items." : "Nothing scheduled for this day.");
        foreach (CollaborationTask task in day) TaskCard(task, false);

        var undated = tasks.FindAll(t => !TryDate(t, out _));
        if (undated.Count > 0)
        {
            Heading("No date");
            foreach (CollaborationTask task in undated) TaskCard(task, true);
        }
    }

    private List<CollaborationTask> TasksOn(DateTime date)
    {
        var day = tasks.FindAll(t => TryDate(t, out DateTime d) && d == date);
        // All-day items first, then by start time, like Google Calendar.
        day.Sort((a, b) => string.CompareOrdinal(a.start_time ?? "", b.start_time ?? ""));
        return day;
    }

    private static bool TryDate(CollaborationTask task, out DateTime date) =>
        DateTime.TryParseExact(task?.deadline_date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static Color32 ColorFor(string owner)
    {
        int hash = 0;
        foreach (char ch in owner ?? "") hash = unchecked(hash * 31 + ch);
        return Palette[(hash & int.MaxValue) % Palette.Length];
    }

    private static DateTime FirstOfMonth(DateTime date) => new(date.Year, date.Month, 1);

    // ---------- building blocks ----------

    private DayCell CreateCell(RectTransform body, int index)
    {
        int col = index % 7, row = index / 7;
        RectTransform rect = Rect("Day", body);
        Stretch(rect, new Vector2(col / 7f, 1 - (row + 1) / 6f), new Vector2((col + 1) / 7f, 1 - row / 6f),
            new Vector2(.5f, .5f), new Vector2(-.5f, -.5f));
        var cell = new DayCell { Background = Fill(rect.gameObject, Cell, true) };
        var button = rect.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() => { selected = cell.Date; if (cell.Date.Month != month.Month) month = FirstOfMonth(cell.Date); Render(); });

        RectTransform circle = Rect("Date", rect);
        circle.anchorMin = circle.anchorMax = new Vector2(.5f, 1);
        circle.pivot = new Vector2(.5f, 1);
        circle.anchoredPosition = new Vector2(0, -4);
        cell.Circle = circle.gameObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(cell.Circle, TodayFill, true);
        cell.Circle.raycastTarget = false;
        cell.Number = Label(rect, "", 14, TextPrimary, TextAnchor.MiddleCenter);
        cell.Number.rectTransform.anchorMin = cell.Number.rectTransform.anchorMax = new Vector2(.5f, 1);
        cell.Number.rectTransform.pivot = new Vector2(.5f, 1);
        cell.Number.rectTransform.anchoredPosition = new Vector2(0, -4);
        cell.Number.rectTransform.sizeDelta = new Vector2(80, 28);

        cell.Chips = Rect("Events", rect);
        Stretch(cell.Chips, Vector2.zero, Vector2.one, new Vector2(3, 16), new Vector2(-3, -35));
        cell.More = Label(rect, "", 12, TextMuted, TextAnchor.LowerLeft);
        Stretch(cell.More.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(6, 1), new Vector2(-4, 16));
        return cell;
    }

    private void Chip(RectTransform parent, CollaborationTask task, int index)
    {
        RectTransform chip = Rect("Event", parent);
        Stretch(chip, new Vector2(0, 1), Vector2.one, new Vector2(0, -22 * (index + 1)), new Vector2(0, -22 * index - 2));
        var image = chip.gameObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(image, ColorFor(task.owner));
        image.raycastTarget = false;
        string time = task.start_time ?? "";
        Text title = Label(chip, (time.Length > 0 ? time + " " : "") + task.title, 12, Color.white, TextAnchor.MiddleLeft);
        title.horizontalOverflow = HorizontalWrapMode.Overflow;
        Stretch(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(6, 0), new Vector2(-4, 0));
        chip.gameObject.AddComponent<RectMask2D>();
    }

    private void TaskCard(CollaborationTask task, bool undated)
    {
        RectTransform card = Rect("Task", sidebarContent);
        card.gameObject.AddComponent<LayoutElement>().preferredHeight = 86;
        LiveKitMeetingStyle.ApplyRounded(card.gameObject.AddComponent<Image>(), Card);
        RectTransform stripe = Rect("Colour", card);
        Stretch(stripe, Vector2.zero, new Vector2(0, 1), new Vector2(8, 10), new Vector2(14, -10));
        LiveKitMeetingStyle.ApplyRounded(stripe.gameObject.AddComponent<Image>(), ColorFor(task.owner), true);
        Text title = Label(card, task.title, 17, TextPrimary, TextAnchor.UpperLeft);
        Stretch(title.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(24, -36), new Vector2(-10, -8));
        string slot = TimeRange(task);
        string due = undated ? task.deadline : slot.Length > 0 ? $"{task.deadline_date}  {slot}" : $"{task.deadline} ({task.deadline_date})";
        Text detail = Label(card, $"Owner: {task.owner}\nDue: {due}", 14, TextMuted, TextAnchor.UpperLeft);
        Stretch(detail.rectTransform, Vector2.zero, Vector2.one, new Vector2(24, 6), new Vector2(-10, -38));
    }

    private void Heading(string text)
    {
        Text label = Label(sidebarContent, text, 16, TextMuted, TextAnchor.LowerLeft);
        label.gameObject.AddComponent<LayoutElement>().preferredHeight = 34;
    }

    private void Note(string text)
    {
        Text label = Label(sidebarContent, text, 15, TextMuted, TextAnchor.UpperLeft);
        label.gameObject.AddComponent<LayoutElement>().preferredHeight = 70;
    }

    private RectTransform ScrollList(RectTransform parent)
    {
        RectTransform view = Rect("Scroll", parent);
        Stretch(view, Vector2.zero, Vector2.one, new Vector2(12, 12), new Vector2(-12, -56));
        Fill(view.gameObject, new Color32(0, 0, 0, 0), true);
        view.gameObject.AddComponent<RectMask2D>();
        RectTransform content = Rect("Content", view);
        Stretch(content, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero);
        content.pivot = new Vector2(.5f, 1);
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 8;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.content = content; scroll.viewport = view; scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 28;
        return content;
    }

    private void AddButton(RectTransform parent, string title, Vector2 position, Vector2 size, Color32 color,
        bool outlined, Action click)
    {
        RectTransform rect = Rect(title, parent);
        Place(rect, position, size);
        var image = rect.gameObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(image, color, true);
        if (outlined) rect.gameObject.AddComponent<Outline>().effectColor = GridLine;
        var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
        button.onClick.AddListener(() => click());
        Text label = Label(rect, title, title.Length == 1 ? 24 : 16, TextPrimary, TextAnchor.MiddleCenter);
        Stretch(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
    }

    private Text Label(Transform parent, string value, int size, Color color, TextAnchor anchor)
    {
        RectTransform rect = Rect("Text", parent);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font; text.fontSize = size; text.color = color; text.alignment = anchor; text.text = value;
        text.supportRichText = false; text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private static Image Fill(GameObject target, Color32 color, bool raycast)
    {
        var image = target.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Place(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private static void Stretch(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
    { rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax; }
}
