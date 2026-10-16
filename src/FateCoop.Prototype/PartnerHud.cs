using System.Linq;
using Cleverous.VaultInventory;
using HarmonyLib;
using Tableflip;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int MaxHudRows = 7;
    private static readonly AccessTools.FieldRef<PlayerUI, PartyUI> PartyUiRef = AccessTools.FieldRefAccess<PlayerUI, PartyUI>("partyUI");
    private static readonly AccessTools.FieldRef<PartyUI, PartyPetUI> PetSlotRef = AccessTools.FieldRefAccess<PartyUI, PartyPetUI>("petUI");
    private static readonly AccessTools.FieldRef<PartyUI, PartyMemberUI[]> AllySlotsRef = AccessTools.FieldRefAccess<PartyUI, PartyMemberUI[]>("partyMemberUIs");
    private static readonly AccessTools.FieldRef<PartyUI, UiBlockingPanel> PartyBlockerRef = AccessTools.FieldRefAccess<PartyUI, UiBlockingPanel>("controllerInputBlocker");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, Image> HpRingRef = AccessTools.FieldRefAccess<PartyCharacterUI, Image>("hpMeter");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, Image> ManaRingRef = AccessTools.FieldRefAccess<PartyCharacterUI, Image>("manaMeter");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, TextMeshProUGUI> SlotNameRef = AccessTools.FieldRefAccess<PartyCharacterUI, TextMeshProUGUI>("nameText");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, RawImage> SlotPictureRef = AccessTools.FieldRefAccess<PartyCharacterUI, RawImage>("profilePictureImage");
    private PlayerUI? hudOwner;
    private float nextHudSearch;

    private static T CloneSlot<T>(T source, Transform before) where T : PartyCharacterUI
    {
        var clone = Object.Instantiate(source.gameObject, source.transform.parent).GetComponent<T>();
        clone.transform.SetSiblingIndex(before.GetSiblingIndex());
        clone.enabled = false;
        clone.navigation = new Navigation { mode = Navigation.Mode.None };
        clone.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
        foreach (var button in clone.GetComponentsInChildren<Button>(true)) button.enabled = false;
        clone.gameObject.SetActive(false);
        return clone;
    }

    private static void FillRing(PartyCharacterUI slot, int hp, int maxHp, int mana, int maxMana)
    {
        HpRingRef(slot).fillAmount = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) * 0.5f : 0f;
        ManaRingRef(slot).fillAmount = maxMana > 0 ? Mathf.Clamp01((float)mana / maxMana) * 0.5f : 0f;
    }

    private void UpdatePartnerHud()
    {
        if (hudOwner == null)
        {
            if (Time.unscaledTime < nextHudSearch || Player.PlayerChar == null) return;
            nextHudSearch = Time.unscaledTime + 1f;
            hudOwner = Object.FindObjectOfType<PlayerUI>();
            if (hudOwner == null) return;
            foreach (var partner in partners.Values) partner.DestroyHud();
        }
        UpdateProgressBar();
        var party = PartyUiRef(hudOwner);
        var allies = AllySlotsRef(party);
        var blocker = PartyBlockerRef(party).transform;
        party.GetComponent<VerticalLayoutGroup>().childScaleHeight = true;
        var gameRows = allies.Count(slot => slot.gameObject.activeSelf) + (PetSlotRef(party).gameObject.activeSelf ? 1 : 0);
        var visible = partners.Values.Where(p => p.Frame != null && p.Name.Length > 0).ToList();
        var showPets = visible.Count * 2 + gameRows <= MaxHudRows;
        var scale = Mathf.Min(1f, (float)(MaxHudRows - gameRows) / (visible.Count * (showPets ? 2 : 1)));
        foreach (var partner in partners.Values.Except(visible)) partner.DestroyHud();
        foreach (var partner in visible)
        {
            if (partner.Slot == null)
            {
                partner.Slot = CloneSlot(allies[0], blocker);
                partner.PetSlot = CloneSlot(PetSlotRef(party), blocker);
                ((PartyPetUI)partner.PetSlot).OnInputBehaviorChange(SingletonBehaviour<Cleverous.Prodigy.Input.InputBroker>.Instance.LocalInputBehavior);
                ManaRingRef(partner.PetSlot).fillAmount = 0f;
                partner.SlotPetPortrait = null;
            }
            var frame = partner.Frame!;
            partner.Slot.transform.localScale = Vector3.one * scale;
            partner.Slot.gameObject.SetActive(showPanel);
            var theirs = PartnerPlace(frame);
            var place = PartnerDown(partner) ? "Down" : partner.InWorld(LocalFrameWorld()) ? "" : theirs.Length > 0 ? DescribePlace(theirs) : "Travelling";
            SlotNameRef(partner.Slot).text = partner.Name + " <size=75%>Lv " + frame.level + "</size>" + (place.Length > 0 ? "\n<size=70%>" + place + "</size>" : "");
            FillRing(partner.Slot, frame.hp, frame.maxHp, frame.mana, frame.maxMana);
            if (partner.Portrait != null) SlotPictureRef(partner.Slot).texture = partner.Portrait;
            var pet = JsonUtility.FromJson<PetSnapshot>(frame.petJson);
            var petShown = showPets && showPanel && pet != null && pet.present;
            partner.PetSlot!.gameObject.SetActive(petShown);
            if (!petShown) continue;
            partner.PetSlot.transform.localScale = Vector3.one * scale;
            SlotNameRef(partner.PetSlot).text = pet!.name;
            FillRing(partner.PetSlot, pet.hp, pet.maxHp, 0, 0);
            if (pet.portrait == partner.SlotPetPortrait) continue;
            partner.SlotPetPortrait = pet.portrait;
            SlotPictureRef(partner.PetSlot).texture = SingletonBehaviour<FateDB>.Instance.PetProfilePictures.SelectMany(p => new[] { p.regular, p.shadowed }).FirstOrDefault(t => t != null && t.name == pet.portrait);
        }
    }

    private static void KeepPortrait(Partner partner, Texture2D picture)
    {
        if (picture == null) return;
        if (partner.Portrait != null) Object.Destroy(partner.Portrait);
        partner.Portrait = Object.Instantiate(picture);
    }
}
