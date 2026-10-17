using System.Collections.Generic;
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
    private const float ScrollSpeed = 35f;
    private static readonly AccessTools.FieldRef<QuestUI, QuestEntryUI[]> SideQuestRowsRef = AccessTools.FieldRefAccess<QuestUI, QuestEntryUI[]>("sideQuests");
    private static readonly AccessTools.FieldRef<QuestUI, QuestEntryUI> MainQuestRowRef = AccessTools.FieldRefAccess<QuestUI, QuestEntryUI>("mainQuest");
    private static readonly AccessTools.FieldRef<QuestUI, RealmData.RealmId> QuestLogRealmRef = AccessTools.FieldRefAccess<QuestUI, RealmData.RealmId>("activeRealm");
    private static readonly AccessTools.FieldRef<QuestUI, Button> QuestLogLeftRef = AccessTools.FieldRefAccess<QuestUI, Button>("realmLeftButton");
    private static readonly AccessTools.FieldRef<QuestUI, Button> QuestLogRightRef = AccessTools.FieldRefAccess<QuestUI, Button>("realmRightButton");
    private static readonly System.Reflection.MethodInfo BuildQuestLogMethod = AccessTools.Method(typeof(QuestUI), "BuildQuestUIForRealm");
    private static bool buildingQuestLog;

    private void InstallQuestLogHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(QuestManager), "IsQuestLogFull"), prefix: new HarmonyMethod(typeof(Plugin), nameof(NeverFull)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "IsQuestLogFullActiveTownRealm"), prefix: new HarmonyMethod(typeof(Plugin), nameof(NeverFull)));
        harmony.Patch(AccessTools.Method(typeof(QuestUI), "BuildQuestUIForRealm"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestLog)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestLog)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestLogBuilt)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), nameof(QuestManager.GetQuests), new[] { typeof(int) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestLogSideQuests)));
        harmony.Patch(AccessTools.Method(typeof(QuestUI), "OnEnable"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestLogOpened)));
        harmony.Patch(AccessTools.Method(typeof(QuestUI), "OnRealmLeftButton"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestLogLeft)));
        harmony.Patch(AccessTools.Method(typeof(QuestUI), "OnRealmRightButton"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestLogRight)));
    }

    private static List<RealmData.RealmId> ReachedRunRealms() =>
        RunSections.Select(section => section.realm).Where(realm => realm == RunSections[0].realm || SaveData.CurrentSaveData.GetRealmSave(realm).maxDepthReached > 0).ToList();

    private static void BeforeQuestLogSideQuests(ref int realmId)
    {
        if (buildingQuestLog && InRun((RealmData.RealmId)realmId)) realmId = (int)RunSections[0].realm;
    }

    private static void AfterQuestLogOpened(QuestUI __instance)
    {
        if (!InRun(RunSections[0].realm)) return;
        var paged = ReachedRunRealms().Count > 1;
        QuestLogLeftRef(__instance).gameObject.SetActive(paged);
        QuestLogRightRef(__instance).gameObject.SetActive(paged);
    }

    private static bool BeforeQuestLogLeft(QuestUI __instance) => PageQuestLog(__instance, -1);

    private static bool BeforeQuestLogRight(QuestUI __instance) => PageQuestLog(__instance, 1);

    private static bool PageQuestLog(QuestUI log, int step)
    {
        if (!InRun(QuestLogRealmRef(log))) return true;
        var realms = ReachedRunRealms();
        var next = (realms.IndexOf(QuestLogRealmRef(log)) + step + realms.Count) % realms.Count;
        BuildQuestLogMethod.Invoke(log, new object[] { realms[next] });
        return false;
    }

    private static bool NeverFull(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static int ShownQuestCount(RealmData.RealmId realm) =>
        SingletonBehaviour<QuestManager>.Instance.GetQuests((int)realm)?.Count(q => q != null && !string.IsNullOrEmpty(q.QuestGiverName) && q.Status != Quest.QuestStatus.None) ?? 0;

    private static void AfterQuestLogBuilt() => buildingQuestLog = false;

    private static void BeforeQuestLog(QuestUI __instance, RealmData.RealmId realm)
    {
        buildingQuestLog = true;
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
        var scroll = CreateScrollList("FateCoop_SideQuestScroll", group, spacing);
        var viewport = scroll.viewport;
        viewport.SetSiblingIndex(rowRect.GetSiblingIndex());
        viewport.anchorMin = viewport.anchorMax = rowRect.anchorMin;
        viewport.pivot = rowRect.pivot;
        viewport.sizeDelta = new Vector2(rowRect.sizeDelta.x, VisibleQuestRows * rowRect.sizeDelta.y + (VisibleQuestRows - 1) * spacing);
        var layout = scroll.content.GetComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = layout.childControlHeight = layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        foreach (var row in rows) row.transform.SetParent(scroll.content, false);
        return scroll.content;
    }

    private static ScrollRect CreateScrollList(string name, Transform parent, float spacing)
    {
        var viewport = new GameObject(name, typeof(RectTransform), typeof(RectMask2D), typeof(ScrollRect)).GetComponent<RectTransform>();
        viewport.SetParent(parent, false);
        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        content.GetComponent<VerticalLayoutGroup>().spacing = spacing;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = viewport.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = ScrollSpeed;
        scroll.viewport = viewport;
        scroll.content = content;
        viewport.gameObject.AddComponent<ScrollFollower>().Scroll = scroll;
        return scroll;
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

public sealed class ScrollFollower : MonoBehaviour
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
