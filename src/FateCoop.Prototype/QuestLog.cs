using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int VisibleQuestRows = 3;
    private const float QuestScrollSpeed = 35f;
    private static readonly AccessTools.FieldRef<QuestUI, QuestEntryUI[]> SideQuestRowsRef = AccessTools.FieldRefAccess<QuestUI, QuestEntryUI[]>("sideQuests");
    private static readonly AccessTools.FieldRef<QuestUI, QuestEntryUI> MainQuestRowRef = AccessTools.FieldRefAccess<QuestUI, QuestEntryUI>("mainQuest");

    private void InstallQuestLogHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(QuestManager), "IsQuestLogFull"), prefix: new HarmonyMethod(typeof(Plugin), nameof(NeverFull)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "IsQuestLogFullActiveTownRealm"), prefix: new HarmonyMethod(typeof(Plugin), nameof(NeverFull)));
        harmony.Patch(AccessTools.Method(typeof(QuestUI), "BuildQuestUIForRealm"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestLog)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestLog)));
    }

    private static bool NeverFull(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static int ShownQuestCount(RealmData.RealmId realm) =>
        SingletonBehaviour<QuestManager>.Instance.GetQuests((int)realm)?.Count(q => q != null && !string.IsNullOrEmpty(q.QuestGiverName) && q.Status != Quest.QuestStatus.None) ?? 0;

    private static void BeforeQuestLog(QuestUI __instance, RealmData.RealmId realm)
    {
        var rows = SideQuestRowsRef(__instance);
        var content = QuestScrollContent(__instance, rows);
        var wanted = Mathf.Max(VisibleQuestRows, ShownQuestCount(realm));
        if (rows.Length >= wanted) return;
        var grown = rows.ToList();
        while (grown.Count < wanted)
        {
            var row = Object.Instantiate(rows[0], content);
            row.gameObject.SetActive(true);
            grown.Add(row);
        }
        SideQuestRowsRef(__instance) = grown.ToArray();
    }

    private static Transform QuestScrollContent(QuestUI log, QuestEntryUI[] rows)
    {
        var existing = log.GetComponentInChildren<ScrollRect>(true);
        if (existing != null) return existing.content;
        var group = rows[0].transform.parent;
        var rowRect = (RectTransform)rows[0].transform;
        var spacing = group.GetComponent<VerticalLayoutGroup>().spacing;
        var viewport = new GameObject("FateCoop_SideQuestScroll", typeof(RectTransform), typeof(RectMask2D), typeof(ScrollRect)).GetComponent<RectTransform>();
        viewport.SetParent(group, false);
        viewport.SetSiblingIndex(rowRect.GetSiblingIndex());
        viewport.anchorMin = viewport.anchorMax = rowRect.anchorMin;
        viewport.pivot = rowRect.pivot;
        viewport.sizeDelta = new Vector2(rowRect.sizeDelta.x, VisibleQuestRows * rowRect.sizeDelta.y + (VisibleQuestRows - 1) * spacing);
        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        var layout = content.GetComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = layout.childControlHeight = layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = viewport.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = QuestScrollSpeed;
        scroll.viewport = viewport;
        scroll.content = content;
        foreach (var row in rows) row.transform.SetParent(content, false);
        viewport.gameObject.AddComponent<QuestScrollFollower>().Scroll = scroll;
        return content;
    }

    private static void AfterQuestLog(QuestUI __instance, RealmData.RealmId realm)
    {
        var rows = SideQuestRowsRef(__instance);
        var shown = Mathf.Max(VisibleQuestRows, ShownQuestCount(realm));
        for (var i = 0; i < rows.Length; i++) rows[i].gameObject.SetActive(i < shown);
        var active = rows.Take(shown).ToArray();
        var main = MainQuestRowRef(__instance).questButton;
        for (var i = 0; i < active.Length; i++)
        {
            var up = i == 0 ? main : active[i - 1].questButton;
            var down = i + 1 < active.Length ? active[i + 1].questButton : null;
            active[i].questButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = up, selectOnDown = down, selectOnRight = active[i].cancelButton };
            active[i].cancelButton.navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit, selectOnLeft = active[i].questButton,
                selectOnUp = i == 0 ? main : active[i - 1].cancelButton, selectOnDown = i + 1 < active.Length ? active[i + 1].cancelButton : null
            };
        }
        var mainNavigation = main.navigation;
        mainNavigation.mode = Navigation.Mode.Explicit;
        mainNavigation.selectOnDown = active[0].questButton;
        main.navigation = mainNavigation;
        var scroll = __instance.GetComponentInChildren<ScrollRect>(true);
        if (scroll != null) scroll.verticalNormalizedPosition = 1f;
    }
}

public sealed class QuestScrollFollower : MonoBehaviour
{
    public ScrollRect? Scroll;

    private void LateUpdate()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (Scroll == null || selected == null || !selected.transform.IsChildOf(Scroll.content)) return;
        var row = selected.transform;
        while (row.parent != Scroll.content) row = row.parent;
        var viewport = Scroll.viewport;
        var rowRect = (RectTransform)row;
        var top = -rowRect.anchoredPosition.y - rowRect.rect.height * (1f - rowRect.pivot.y);
        var bottom = top + rowRect.rect.height;
        var offset = Scroll.content.anchoredPosition.y;
        if (top < offset) offset = top;
        else if (bottom > offset + viewport.rect.height) offset = bottom - viewport.rect.height;
        Scroll.content.anchoredPosition = new Vector2(Scroll.content.anchoredPosition.x, offset);
    }
}
