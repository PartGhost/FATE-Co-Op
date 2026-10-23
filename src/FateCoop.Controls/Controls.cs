using System.Linq;
using Cleverous.Prodigy;
using Cleverous.Prodigy.Input;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace FateCoop.Controls;

public sealed partial class Plugin
{
    private const float KeyboardStep = 1.5f, StickTurnSpeed = 120f;
    private const string KeyboardScheme = "Keyboard";
    private static readonly System.Reflection.MethodInfo InputBehaviorSetter = AccessTools.PropertySetter(typeof(InputBroker), nameof(InputBroker.LocalInputBehavior));
    private static readonly string[] CameraKeys = { "<Keyboard>/w", "<Keyboard>/s", "<Keyboard>/a", "<Keyboard>/d" };
    private static readonly AccessTools.FieldRef<ProdigyPlayer, Cleverous.Prodigy.PlayerInput> PlayerInputRef = AccessTools.FieldRefAccess<ProdigyPlayer, Cleverous.Prodigy.PlayerInput>("m_currentInput");
    private static readonly AccessTools.FieldRef<InputBroker, NavMeshAgent> BrokerAgent = AccessTools.FieldRefAccess<InputBroker, NavMeshAgent>("m_navMeshAgent");
    private static readonly AccessTools.FieldRef<InputBroker, bool> BrokerMovementLocked = AccessTools.FieldRefAccess<InputBroker, bool>("movementLocked");
    private static readonly AccessTools.FieldRef<InputBroker, bool> BrokerAttackWhenInRange = AccessTools.FieldRefAccess<InputBroker, bool>("attackWhenInRange");
    private static readonly AccessTools.FieldRef<InputBroker, bool> BrokerInteractWhenInRange = AccessTools.FieldRefAccess<InputBroker, bool>("interactWhenInRange");
    private static readonly System.Reflection.MethodInfo ZoomInputSetter = AccessTools.PropertySetter(typeof(InputBroker), "CameraZoomInputValue");
    private static readonly AccessTools.FieldRef<ProdigyCamera, Quaternion> CameraOriginalRotation = AccessTools.FieldRefAccess<ProdigyCamera, Quaternion>("originalRotation");
    private static readonly AccessTools.FieldRef<ProdigyCamera, float> CameraOriginalYaw = AccessTools.FieldRefAccess<ProdigyCamera, float>("originalYRotation");
    private static readonly AccessTools.FieldRef<MiniMapIconUI, Quaternion> SavedIconRotation = AccessTools.FieldRefAccess<MiniMapIconUI, Quaternion>("savedRotation");
    private bool keyboardMovement, keyboardMoving, cameraKeysRemoved;
    private float cameraTurnSpeed, cameraYaw;
    private Vector2 dragStart, testMove;
    private float testTurn;

    private void InstallControlHooks()
    {
        keyboardMovement = Config.Bind("Controls", "KeyboardMovement", true, "WASD moves your character (camera-relative). Mouse wheel zooms, hold the middle mouse button to turn the camera.").Value;
        cameraTurnSpeed = Config.Bind("Controls", "CameraTurnSpeed", 0.25f, "Degrees the camera turns per pixel of mouse movement while the middle button is held.").Value;
        if (!keyboardMovement) return;
        harmony!.Patch(AccessTools.Method(typeof(InputBroker), "ParseClickToMove"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeClickToMove)));
        harmony.Patch(AccessTools.Method(typeof(InputBroker), "ReadPressedInputs"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterReadInputs)));
        harmony.Patch(AccessTools.Method(typeof(ProdigyCamera), "UpdateCameraRotation"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCameraRotation)));
        harmony.Patch(AccessTools.Method(typeof(MiniMapIconUI), "LateUpdate"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterMinimapIcon)));
        harmony.Patch(AccessTools.Method(typeof(ProdigyPlayer), "LocalClientCollectInput"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCollectInput)));
        harmony.Patch(AccessTools.Method(typeof(InputBroker), "InputActionChangeCallback"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeInputModeCheck)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterInputModeCheck)));
    }

    private static bool BeforeCollectInput(ProdigyPlayer __instance)
    {
        var plugin = Active;
        if (plugin == null || !plugin.keyboardMoving || !InputBlocker.Blocked) return true;
        var walking = BrokerAgent(SingletonBehaviour<InputBroker>.Instance).desiredVelocity;
        PlayerInputRef(__instance).Set(walking, walking, walking, interact: false, altInteract: false, cycle: false, fire1: false, fire2: false, rage: false);
        return false;
    }

    private bool MovementBlocked => InputBlocker.AnyBlockingUiMenuIsOpen || InventoryUI.IsDraggingItem || (Player.PlayerChar != null && Player.PlayerChar.MyVitals.IsDead);

    private static void BeforeInputModeCheck(InputBroker __instance, out InputBroker.InputBehavior __state) => __state = __instance.LocalInputBehavior;

    private static void AfterInputModeCheck(InputBroker __instance, object obj, InputBroker.InputBehavior __state)
    {
        if (__state == InputBroker.InputBehavior.Free || __instance.LocalInputBehavior != InputBroker.InputBehavior.Free || !(obj is InputAction { activeControl: { } control })) return;
        Active?.Logger.LogWarning($"Controller mode switched on by {control.device.displayName} ({control.device.layout}, {control.path})");
    }

    private static void UseKeyboardControls(InputBroker broker)
    {
        InputBehaviorSetter.Invoke(broker, new object[] { InputBroker.InputBehavior.ClickToMove });
        broker.Inputs.bindingMask = InputBinding.MaskByGroup(broker.Inputs.controlSchemes.First(scheme => scheme.name == KeyboardScheme).bindingGroup);
        broker.OnInputBehaviorChanged?.Invoke(InputBroker.InputBehavior.ClickToMove);
    }

    private static void AfterMinimapIcon(MiniMapIconUI __instance)
    {
        if (Active == null) return;
        var saved = SavedIconRotation(__instance);
        if (__instance.isCamera)
        {
            __instance.transform.rotation = Quaternion.AngleAxis(Active.cameraYaw, Vector3.up) * saved;
            return;
        }
        var top = saved * Vector3.up;
        var savedYaw = Mathf.Atan2(top.x, top.z) * Mathf.Rad2Deg;
        __instance.transform.rotation = Quaternion.AngleAxis(ProdigyCamera.GameCamera.transform.eulerAngles.y - savedYaw, Vector3.up) * saved;
    }

    private static void AfterReadInputs(InputBroker __instance)
    {
        var plugin = Active;
        if (plugin == null) return;
        if (!plugin.cameraKeysRemoved && __instance.Inputs != null)
        {
            foreach (var action in new[] { __instance.Inputs.CharacterInputs.CameraZoom, __instance.Inputs.CharacterInputs.CameraRotate })
                for (var i = 0; i < action.bindings.Count; i++)
                    if (CameraKeys.Contains(action.bindings[i].path)) action.ApplyBindingOverride(i, "");
            plugin.cameraKeysRemoved = true;
        }
        var keyboard = Keyboard.current;
        if (__instance.LocalInputBehavior == InputBroker.InputBehavior.Free && keyboard != null && keyboard.anyKey.wasPressedThisFrame) UseKeyboardControls(__instance);
        if (__instance.LocalInputBehavior == InputBroker.InputBehavior.ClickToMove) ZoomInputSetter.Invoke(__instance, new object[] { 0f });
    }

    private static void BeforeClickToMove(InputBroker __instance)
    {
        var plugin = Active;
        var player = Player.PlayerChar;
        var agent = BrokerAgent(__instance);
        if (plugin == null || player == null || agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        var testing = plugin.testMove != Vector2.zero;
        if (testing) BrokerMovementLocked(__instance) = false;
        var input = testing ? plugin.testMove : __instance.Inputs.Persistent.Move.ReadValue<Vector2>();
        var allowed = input.sqrMagnitude > 0.01f && !plugin.MovementBlocked && __instance.ActiveInputMode == InputBroker.InputMode.Game && !BrokerMovementLocked(__instance)
            && !player.PerformingAttack() && !player.PerformingCast() && !player.Fishing();
        if (!allowed)
        {
            if (plugin.keyboardMoving && input.sqrMagnitude <= 0.01f) agent.ResetPath();
            plugin.keyboardMoving = false;
            return;
        }
        var camera = ProdigyCamera.GameCamera.transform;
        var forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
        var right = Vector3.ProjectOnPlane(camera.right, Vector3.up).normalized;
        var direction = Vector3.ClampMagnitude(forward * input.y + right * input.x, 1f);
        if (!NavMesh.SamplePosition(player.transform.position + direction * KeyboardStep, out var hit, KeyboardStep, NavMesh.AllAreas)) return;
        BrokerAttackWhenInRange(__instance) = false;
        BrokerInteractWhenInRange(__instance) = false;
        __instance.SetMoveDestination(hit.position);
        plugin.keyboardMoving = true;
    }

    private static bool BeforeCameraRotation(ProdigyCamera __instance)
    {
        var plugin = Active;
        var mouse = Mouse.current;
        if (plugin == null || mouse == null) return true;
        if (mouse.middleButton.wasPressedThisFrame) plugin.dragStart = mouse.position.ReadValue();
        if (mouse.middleButton.isPressed && !plugin.MovementBlocked) plugin.cameraYaw += mouse.delta.ReadValue().x * plugin.cameraTurnSpeed;
        var broker = SingletonBehaviour<InputBroker>.Instance;
        if (!broker.HoldingSpellWheel) plugin.cameraYaw -= broker.CameraRotateInputValue * StickTurnSpeed * Time.unscaledDeltaTime;
        plugin.cameraYaw += plugin.testTurn;
        if (mouse.middleButton.wasReleasedThisFrame) mouse.WarpCursorPosition(plugin.dragStart);
        var original = CameraOriginalRotation(__instance).eulerAngles;
        __instance.transform.localRotation = Quaternion.Euler(original.x, CameraOriginalYaw(__instance) + plugin.cameraYaw, original.z);
        return false;
    }
}
