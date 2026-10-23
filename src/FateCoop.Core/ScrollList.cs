using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FateCoop.Core;

public static class ScrollList
{
    private const float ScrollSpeed = 35f;

    public static ScrollRect Create(string name, Transform parent, float spacing)
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
