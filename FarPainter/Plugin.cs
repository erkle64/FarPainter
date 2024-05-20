using C3.ModKit;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using Unfoundry;
using UnityEngine;

namespace FarPainter
{
    [UnfoundryMod(GUID)]
    public class Plugin : UnfoundryPlugin
    {
        public const string
            MODNAME = "FarPainter",
            AUTHOR = "erkle64",
            GUID = AUTHOR + "." + MODNAME,
            VERSION = "0.1.0";

        public static LogSource log;

        public static TypedConfigEntry<float> paintRange;

        private static bool _bulkPaintDragging = false;
        private static float _bulkPaintStartTime = 0.0f;
        private static Vector3Int _bulkPaintStartPosition = Vector3Int.zero;
        private static BulkPaintOrientation _bulkPaintOrientation = BulkPaintOrientation.X;
        private static Plane _bulkPaintPlane = new Plane();
        private static readonly List<BuildableObjectGO> _bulkPaintQueryResult = new List<BuildableObjectGO>(0);

        private enum BulkPaintOrientation
        {
            X,
            Y,
            Z
        }

        public Plugin()
        {
            log = new LogSource(MODNAME);

            new Config(GUID)
                .Group("General")
                    .Entry(out paintRange, "Paint Range", 50.0f, "The range of the paint roller.")
                .EndGroup()
                .Load()
                .Save();
        }

        public override void Load(Mod mod)
        {
            log.Log($"Loading {MODNAME}");

            if (typeof(ColorToolHH).GetMethod("updateSelectedColorTint", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing updateSelectedColorTint");
            if (typeof(ColorToolHH).GetField("raycastHits", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing raycastHits");
            if (typeof(ColorToolHH).GetField("bobMultiplier", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing bobMultiplier");
            if (typeof(ColorToolHH).GetField("bobTimer", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing bobTimer");
            if (typeof(ColorToolHH).GetField("lastTintColor", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing lastTintColor");
            if (typeof(ColorToolHH).GetField("lastPlayedAudioClipIdx", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing lastPlayedAudioClipIdx");
            if (typeof(ColorToolHH).GetField("lastColorizedObject_entityId", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing lastColorizedObject_entityId");
            if (typeof(ColorToolHH).GetField("lastColorizedObject_isReset", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing lastColorizedObject_isReset");
            if (typeof(ColorToolHH).GetField("lastColorizationTime", BindingFlags.NonPublic | BindingFlags.Instance) == null) log.LogError("Missing lastColorizationTime");
        }

        [HarmonyPatch]
        public static class Patch
        {
            public static readonly MethodInfo updateSelectedColorTint = typeof(ColorToolHH).GetMethod("updateSelectedColorTint", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo raycastHits = typeof(ColorToolHH).GetField("raycastHits", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo bobMultiplier = typeof(ColorToolHH).GetField("bobMultiplier", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo bobTimer = typeof(ColorToolHH).GetField("bobTimer", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo lastTintColor = typeof(ColorToolHH).GetField("lastTintColor", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo lastPlayedAudioClipIdx = typeof(ColorToolHH).GetField("lastPlayedAudioClipIdx", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo lastColorizedObject_entityId = typeof(ColorToolHH).GetField("lastColorizedObject_entityId", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo lastColorizedObject_isReset = typeof(ColorToolHH).GetField("lastColorizedObject_isReset", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo lastColorizationTime = typeof(ColorToolHH).GetField("lastColorizationTime", BindingFlags.NonPublic | BindingFlags.Instance);

            [HarmonyPatch(typeof(ColorToolHH), nameof(ColorToolHH._updateBehavoir))]
            [HarmonyPrefix]
            public static bool ColorToolHH_updateBehavoir(ColorToolHH __instance)
            {
                updateSelectedColorTint.Invoke(__instance, null);
                if (!__instance.relatedCharacter.sessionOnly_isClientCharacter || !__instance.isClientCharacterEquip)
                    return false;
                if (__instance.isClientCharacterEquip)
                {
                    Vector3 localPosition = __instance.transform.localPosition;
                    bobMultiplier.SetValue(__instance, (float)bobMultiplier.GetValue(__instance) * 0.97f + 0.03f);
                    bobTimer.SetValue(__instance, (float)bobTimer.GetValue(__instance) + Time.deltaTime * (float)bobMultiplier.GetValue(__instance));
                    localPosition.y = __instance.defaultPosition.y + (float)(((double)Mathf.PerlinNoise(0.0f, (float)bobTimer.GetValue(__instance)) * 2.0 - 1.0) * 0.00749999983236194);
                    __instance.transform.localPosition = localPosition;
                }
                bool allowAction = true;
                bool allowAlternateAction = true;
                if (GlobalStateManager.checkIfCursorIsRequired())
                {
                    allowAction = false;
                    allowAlternateAction = false;
                    _bulkPaintDragging = false;
                }
                if (GameRoot.getClientRenderCharacter().isLookingAtInteractibleObject(out GameObject _))
                    allowAction = false;
                if (ScreenPanelRaycaster.isClientCharacterLookingAtScreenPanel())
                    allowAction = false;
                bool actionHeld = allowAction && GlobalStateManager.getRewiredPlayer0().GetButton("Action");
                if (allowAlternateAction && GlobalStateManager.getRewiredPlayer0().GetButtonDown("Alternate Action"))
                    ColorToolFrame.showFrame();
                bool modifier1Held = GlobalStateManager.getRewiredPlayer0().GetButton("Modifier 1");
                bool modifier2Held = GlobalStateManager.getRewiredPlayer0().GetButton("Modifier 2");
                bool lookingAtBuilding = false;
                bool lookingAtColorizableBuilding = false;
                Ray ray = new Ray(GameRoot.getMainCamera().transform.position, GameRoot.getMainCamera().transform.forward);
                RaycastHit[] raycastHits = (RaycastHit[])Patch.raycastHits.GetValue(__instance);
                if (_bulkPaintDragging)
                {
                    if (_bulkPaintPlane.Raycast(ray, out var distanceToPlane))
                    {
                        var hitPosition = ray.GetPoint(distanceToPlane);
                        Vector3Int targetCube = _bulkPaintStartPosition;
                        switch (_bulkPaintOrientation)
                        {
                            case BulkPaintOrientation.X:
                                targetCube = new Vector3Int(_bulkPaintStartPosition.x, Mathf.FloorToInt(hitPosition.y), Mathf.FloorToInt(hitPosition.z));
                                break;

                            case BulkPaintOrientation.Y:
                                targetCube = new Vector3Int(Mathf.FloorToInt(hitPosition.x), _bulkPaintStartPosition.y, Mathf.FloorToInt(hitPosition.z));
                                break;

                            case BulkPaintOrientation.Z:
                                targetCube = new Vector3Int(Mathf.FloorToInt(hitPosition.x), Mathf.FloorToInt(hitPosition.y), _bulkPaintStartPosition.z);
                                break;
                        }

                        var diff = targetCube - _bulkPaintStartPosition;
                        var size = new Vector3Int(Mathf.Abs(diff.x) + 1, Mathf.Abs(diff.y) + 1, Mathf.Abs(diff.z) + 1);
                        var x = _bulkPaintOrientation == BulkPaintOrientation.X ? size.z : size.x;
                        var y = _bulkPaintOrientation == BulkPaintOrientation.Y ? size.z : size.y;
                        GameRoot.pushPerFrameHighlighterBox(_bulkPaintStartPosition + (Vector3)diff * 0.5f + new Vector3(0.5f, 0.5f, 0.5f), size, 1);
                        GameRoot.setInfoText(string.Format(
                            "Now point to the opposite end of the area and press {2} to confirm.\nSize: {0}x{1}\n{3} to cancel.",
                            x, y,
                            GameRoot.getHotkeyStringFromAction("Action"),
                            GameRoot.getHotkeyStringFromAction("Alternate Action")));

                        if (GlobalStateManager.getRewiredPlayer0().GetButtonUp("Action") && Time.time > _bulkPaintStartTime + 0.5f)
                        {
                            _bulkPaintDragging = false;

                            byte color_r = (byte)Mathf.Clamp(Mathf.RoundToInt(__instance.relatedCharacter.clientData.lastSelectedColor.r * byte.MaxValue), 0, byte.MaxValue);
                            byte color_g = (byte)Mathf.Clamp(Mathf.RoundToInt(__instance.relatedCharacter.clientData.lastSelectedColor.g * byte.MaxValue), 0, byte.MaxValue);
                            byte color_b = (byte)Mathf.Clamp(Mathf.RoundToInt(__instance.relatedCharacter.clientData.lastSelectedColor.b * byte.MaxValue), 0, byte.MaxValue);

                            var pos = new Vector3Int(Mathf.Min(_bulkPaintStartPosition.x, targetCube.x), Mathf.Min(_bulkPaintStartPosition.y, targetCube.y), Mathf.Min(_bulkPaintStartPosition.z, targetCube.z));
                            AABB3D aabb = ObjectPoolManager.aabb3ds.getObject();
                            aabb.reinitialize(pos.x, pos.y, pos.z, size.x, size.y, size.z);
                            _bulkPaintQueryResult.Clear();
                            StreamingSystem.getBuildableObjectGOQuadtreeArray().queryAABB3D(aabb, _bulkPaintQueryResult, true);
                            if (_bulkPaintQueryResult.Count > 0)
                            {
                                foreach (var bogo in _bulkPaintQueryResult)
                                {
                                    if (bogo is IHasColorManager hasColorManager && hasColorManager.ColorManager.hasAnyColorableObject())
                                    {
                                        GameRoot.addLockstepEvent(new ColorizeObjectEvent(__instance.relatedCharacter.usernameHash, bogo.relatedEntityId, color_r, color_g, color_b, false));
                                        //ActionManager.AddQueuedEvent(() => GameRoot.addLockstepEvent(new ColorizeObjectEvent(__instance.relatedCharacter.usernameHash, bogo.relatedEntityId, color_r, color_g, color_b, false)));
                                    }
                                }
                            }
                            ObjectPoolManager.aabb3ds.returnObject(aabb);

                            lastPlayedAudioClipIdx.SetValue(__instance, (int)lastPlayedAudioClipIdx.GetValue(__instance) + 1);
                            lastPlayedAudioClipIdx.SetValue(__instance, (int)lastPlayedAudioClipIdx.GetValue(__instance) % ResourceDB.resourceLinker.audioClip_paintingStrokes.Length);
                            if (!__instance.audioSource_painting.isPlaying)
                                __instance.audioSource_painting.PlayOneShot(ResourceDB.resourceLinker.audioClip_paintingStrokes[(int)lastPlayedAudioClipIdx.GetValue(__instance)]);
                        }
                        else if(GlobalStateManager.getRewiredPlayer0().GetButtonDown("Alternate Action"))
                        {
                            _bulkPaintDragging = false;
                        }
                    }
                }
                else if (allowAction && modifier1Held)
                {
                    __instance.relatedCharacter.renderCharacter.getVoxelInteractionTarget(paintRange.Get(), out var targetCube, out var faceTarget, out RaycastHit _);
                    if (faceTarget != -1)
                    {
                        GameRoot.pushPerFrameHighlighterBox((Vector3)targetCube + new Vector3(0.5f, 0.5f, 0.5f), Vector3.one, 1);

                        if (GlobalStateManager.getRewiredPlayer0().GetButtonDown("Action"))
                        {
                            switch (faceTarget)
                            {
                                case 0:
                                    _bulkPaintOrientation = BulkPaintOrientation.X;
                                    _bulkPaintPlane = new Plane(Vector3.right, targetCube + new Vector3Int(1, 0, 0));
                                    break;

                                case 1:
                                    _bulkPaintOrientation = BulkPaintOrientation.X;
                                    _bulkPaintPlane = new Plane(Vector3.left, targetCube);
                                    break;

                                case 2:
                                    _bulkPaintOrientation = BulkPaintOrientation.Y;
                                    _bulkPaintPlane = new Plane(Vector3.up, targetCube + new Vector3Int(0, 1, 0));
                                    break;

                                case 3:
                                    _bulkPaintOrientation = BulkPaintOrientation.Y;
                                    _bulkPaintPlane = new Plane(Vector3.down, targetCube);
                                    break;

                                case 4:
                                    _bulkPaintOrientation = BulkPaintOrientation.Z;
                                    _bulkPaintPlane = new Plane(Vector3.forward, targetCube + new Vector3Int(0, 0, 1));
                                    break;

                                case 5:
                                    _bulkPaintOrientation = BulkPaintOrientation.Z;
                                    _bulkPaintPlane = new Plane(Vector3.back, targetCube);
                                    break;
                            }

                            _bulkPaintDragging = true;
                            _bulkPaintStartPosition = targetCube;
                            _bulkPaintStartTime = Time.time;
                        }
                    }
                }
                else
                {
                    int count = Physics.RaycastNonAlloc(ray, raycastHits, paintRange.Get(), GlobalStaticCache.s_LayerMask_BuildableObjectFullSize | GlobalStaticCache.s_LayerMask_BuildableObjectPartialSize);
                    if (count > 0)
                    {
                        BuildableObjectGO componentInParent = raycastHits[raycastHits.findNearestHit(count)].collider.gameObject.GetComponentInParent<BuildableObjectGO>();
                        if (componentInParent != null && componentInParent.template != null)
                        {
                            lookingAtBuilding = true;
                            if (componentInParent is IHasColorManager hasColorManager && hasColorManager.ColorManager.hasAnyColorableObject())
                            {
                                lookingAtColorizableBuilding = true;
                                if (actionHeld)
                                {
                                    bool isReset = false;
                                    if (modifier2Held)
                                        isReset = true;
                                    bool isNewObject = true;
                                    if ((ulong)lastColorizedObject_entityId.GetValue(__instance) == componentInParent.relatedEntityId && (bool)lastColorizedObject_isReset.GetValue(__instance) == isReset && Time.realtimeSinceStartup - (float)lastColorizationTime.GetValue(__instance) < 1.0f)
                                        isNewObject = false;
                                    if (isNewObject)
                                    {
                                        byte color_r = (byte)Mathf.Clamp(Mathf.RoundToInt(__instance.relatedCharacter.clientData.lastSelectedColor.r * byte.MaxValue), 0, byte.MaxValue);
                                        byte color_g = (byte)Mathf.Clamp(Mathf.RoundToInt(__instance.relatedCharacter.clientData.lastSelectedColor.g * byte.MaxValue), 0, byte.MaxValue);
                                        byte color_b = (byte)Mathf.Clamp(Mathf.RoundToInt(__instance.relatedCharacter.clientData.lastSelectedColor.b * byte.MaxValue), 0, byte.MaxValue);
                                        GameRoot.addLockstepEvent(new ColorizeObjectEvent(__instance.relatedCharacter.usernameHash, componentInParent.relatedEntityId, color_r, color_g, color_b, isReset));
                                        lastPlayedAudioClipIdx.SetValue(__instance, (int)lastPlayedAudioClipIdx.GetValue(__instance) + 1);
                                        lastPlayedAudioClipIdx.SetValue(__instance, (int)lastPlayedAudioClipIdx.GetValue(__instance) % ResourceDB.resourceLinker.audioClip_paintingStrokes.Length);
                                        if (!__instance.audioSource_painting.isPlaying)
                                            __instance.audioSource_painting.PlayOneShot(ResourceDB.resourceLinker.audioClip_paintingStrokes[(int)lastPlayedAudioClipIdx.GetValue(__instance)]);
                                        lastColorizedObject_entityId.SetValue(__instance, componentInParent.relatedEntityId);
                                        lastColorizedObject_isReset.SetValue(__instance, isReset);
                                        lastColorizationTime.SetValue(__instance, Time.realtimeSinceStartup);
                                    }
                                }
                            }
                        }
                    }
                    if (lookingAtBuilding)
                    {
                        if (lookingAtColorizableBuilding)
                            GameRoot.setInfoText(
                                string.Format("{0} to colorize. {1} to select color.\n{2}+{3} to reset object to default color.\nHold {4} to use bulk paint mode.",
                                GameRoot.getHotkeyStringFromAction("Action"),
                                GameRoot.getHotkeyStringFromAction("Alternate Action"),
                                GameRoot.getHotkeyStringFromAction("Modifier 2"),
                                GameRoot.getHotkeyStringFromAction("Action"),
                                GameRoot.getHotkeyStringFromAction("Modifier 1")));
                        else
                            GameRoot.setInfoText(PoMgr._po("COLOR_TOOL_INFO_INVALID", "Target object cannot be colored."));
                    }
                    else
                        GameRoot.setInfoText(PoMgr._po("COLOR_TOOL_INFO_NO_TARGET", "Look at objects to apply color, not every object can be colorized."));
                }
                return false;
            }
        }
    }
}




