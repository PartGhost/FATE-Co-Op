using System.Linq;
using HarmonyLib;
using Tableflip;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private static readonly AccessTools.FieldRef<PlayerUI, PartyUI> PartyUiRef = AccessTools.FieldRefAccess<PlayerUI, PartyUI>("partyUI");
    private static readonly AccessTools.FieldRef<PartyUI, PartyPetUI> PetSlotRef = AccessTools.FieldRefAccess<PartyUI, PartyPetUI>("petUI");
    private static readonly AccessTools.FieldRef<PartyUI, PartyMemberUI[]> AllySlotsRef = AccessTools.FieldRefAccess<PartyUI, PartyMemberUI[]>("partyMemberUIs");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, Image> HpRingRef = AccessTools.FieldRefAccess<PartyCharacterUI, Image>("hpMeter");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, Image> ManaRingRef = AccessTools.FieldRefAccess<PartyCharacterUI, Image>("manaMeter");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, TextMeshProUGUI> SlotNameRef = AccessTools.FieldRefAccess<PartyCharacterUI, TextMeshProUGUI>("nameText");
    private static readonly AccessTools.FieldRef<PartyCharacterUI, RawImage> SlotPictureRef = AccessTools.FieldRefAccess<PartyCharacterUI, RawImage>("profilePictureImage");
    private PlayerUI? hudOwner;
    private PartyCharacterUI? partnerSlot, partnerPetSlot;
    private float nextHudSearch;
    private string partnerPetModel = "";

    private static T CloneSlot<T>(T source, Vector2 position) where T : PartyCharacterUI
    {
        var clone = Object.Instantiate(source.gameObject, source.transform.parent).GetComponent<T>();
        clone.enabled = false;
        clone.navigation = new Navigation { mode = Navigation.Mode.None };
        clone.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
        foreach (var button in clone.GetComponentsInChildren<Button>(true)) button.enabled = false;
        ((RectTransform)clone.transform).anchoredPosition = position;
        clone.gameObject.SetActive(false);
        return clone;
    }

    private void BuildPartnerHud()
    {
        if (hudOwner != null || Time.unscaledTime < nextHudSearch || Player.PlayerChar == null) return;
        nextHudSearch = Time.unscaledTime + 1f;
        hudOwner = Object.FindObjectOfType<PlayerUI>();
        if (hudOwner == null) return;
        partnerPetModel = "";
        var party = PartyUiRef(hudOwner);
        var allies = AllySlotsRef(party);
        partnerSlot = CloneSlot(allies[0], ((RectTransform)allies[0].transform).anchoredPosition);
        partnerPetSlot = CloneSlot(PetSlotRef(party), ((RectTransform)allies[1].transform).anchoredPosition);
        ((PartyPetUI)partnerPetSlot).OnInputBehaviorChange(SingletonBehaviour<Cleverous.Prodigy.Input.InputBroker>.Instance.LocalInputBehavior);
        ManaRingRef(partnerPetSlot).fillAmount = 0f;
    }

    private static void FillRing(PartyCharacterUI slot, int hp, int maxHp, int mana, int maxMana)
    {
        HpRingRef(slot).fillAmount = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) * 0.5f : 0f;
        ManaRingRef(slot).fillAmount = maxMana > 0 ? Mathf.Clamp01((float)mana / maxMana) * 0.5f : 0f;
    }

    private void UpdatePartnerHud()
    {
        BuildPartnerHud();
        if (partnerSlot == null || partnerPetSlot == null) return;
        var frame = remoteFrame;
        var visible = peerConnected && frame != null && partnerName.Length > 0 && showPanel;
        partnerSlot.gameObject.SetActive(visible);
        if (!visible)
        {
            partnerPetSlot.gameObject.SetActive(false);
            return;
        }
        var theirs = PartnerPlace(frame);
        var place = frame!.hp <= 0 && frame.maxHp > 0 ? "Down" : frame.worldKey == CurrentWorldKey() && frame.worldKey.Length > 0 ? "" : theirs.Length > 0 ? DescribePlace(theirs) : "Travelling";
        SlotNameRef(partnerSlot).text = partnerName + " <size=75%>Lv " + frame.level + "</size>" + (place.Length > 0 ? "\n<size=70%>" + place + "</size>" : "");
        FillRing(partnerSlot, frame.hp, frame.maxHp, frame.mana, frame.maxMana);
        if (actor != null && actor.GetProfilePicture != null) SlotPictureRef(partnerSlot).texture = actor.GetProfilePicture;
        var pet = JsonUtility.FromJson<PetSnapshot>(frame.petJson);
        partnerPetSlot.gameObject.SetActive(pet != null && pet.present);
        if (pet == null || !pet.present) return;
        SlotNameRef(partnerPetSlot).text = pet.name;
        FillRing(partnerPetSlot, pet.hp, pet.maxHp, 0, 0);
        if (pet.model == partnerPetModel) return;
        partnerPetModel = pet.model;
        var family = SingletonBehaviour<FateDB>.Instance.allMonsters.FirstOrDefault(m => m != null && m.displayName == pet.model)?.family;
        var picture = SingletonBehaviour<FateDB>.Instance.PetProfilePictures.FirstOrDefault(p => p.family == family);
        if (picture != null) SlotPictureRef(partnerPetSlot).texture = picture.regular;
    }
}
