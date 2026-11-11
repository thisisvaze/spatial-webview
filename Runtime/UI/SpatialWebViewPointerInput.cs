using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if SPATIAL_WEBVIEW_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if SPATIAL_WEBVIEW_XR_HANDS
using UnityEngine.XR.Hands;
#endif

namespace SpatialWebView
{
    /// <summary>
    /// Cross-headset & smart-glasses pointer, fingertip-proximity, grab-handle, and pulsed haptic router:
    /// 1) Unity EventSystem / XRI / PointableCanvas / Android XR UI modules (native IPointer* events).
    /// 2) Meta Horizon OS (Quest 2, Pro, 3, 3S): OVRHand index pinch + OVRInput controller ray & thumbstick.
    /// 3) Vendor-neutral OpenXR (Pico, Android XR, Vive): Unity.XR.Hands pinch ray + UnityEngine.XR controllers.
    /// 4) AR/VR smart glasses (XREAL, Viture, RayNeo): optional head-gaze + phone tap / Bluetooth clicker fallback.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialWebViewPointerInput :
        MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IDragHandler,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerMoveHandler,
        IScrollHandler
    {
        private const float PinchDownMeters = 0.020f;
        private const float PinchReleaseMeters = 0.028f;

        [SerializeField] private SpatialWebViewWindow window;
        [SerializeField] private Transform viewportTransform;
        [SerializeField] private Collider viewportCollider;
        [SerializeField] private RectTransform dockRect;
        [SerializeField] private RectTransform grabHandleRect;
        [SerializeField] private bool enableBuiltInRayInput = true;
        [SerializeField] private bool enableGlassesGazeFallback = true;
        [SerializeField] private float maxRayDistance = 6.0f;
        [SerializeField] private float thumbstickScrollSpeed = 6.5f;

        private RectTransform selfRect;
        private RectTransform cursorDotRect;
        private SpatialPillControl hoveredPill;
        private SpatialPillControl pressedPill;

        private int lastEventSystemFrame = -10;
        private bool builtInHoveringViewport;
        private bool builtInPressingViewport;
        private bool draggingBoardHandle;
        private bool xrHandPinchLatched;
        private float grabDistanceMeters = 1.25f;
        private Vector3 grabLocalOffset;
        private Vector3 boardMoveVelocity;
        private Vector2 lastUv = new Vector2(0.5f, 0.5f);

        private readonly List<UnityEngine.XR.InputDevice> xrControllers = new List<UnityEngine.XR.InputDevice>(2);
        private UnityEngine.XR.InputDevice buzzingXrDevice;
        private bool xrBuzzActive;
        private float buzzAmplitude;
        private float buzzUntilTime;
        private int buzzFrameCounter;

#if SPATIAL_WEBVIEW_XR_HANDS
        private static readonly List<XRHandSubsystem> s_HandSubsystems = new List<XRHandSubsystem>(2);
#endif

#if SPATIAL_WEBVIEW_META_OVR
        private OVRCameraRig cachedCameraRig;
        private OVRHand[] cachedHands;
        private float nextRigLookupTime;
        private OVRInput.Controller buzzingOvrController = OVRInput.Controller.None;
#endif

        public bool EnableBuiltInRayInput
        {
            get => enableBuiltInRayInput;
            set => enableBuiltInRayInput = value;
        }

        public bool EnableGlassesGazeFallback
        {
            get => enableGlassesGazeFallback;
            set => enableGlassesGazeFallback = value;
        }

        public bool IsHoldingBoardHandle => draggingBoardHandle;

        public void Bind(
            SpatialWebViewWindow ownerWindow,
            Transform viewport,
            Collider colliderComponent,
            RectTransform dock,
            RectTransform grabHandle)
        {
            window = ownerWindow;
            viewportTransform = viewport;
            viewportCollider = colliderComponent;
            dockRect = dock;
            grabHandleRect = grabHandle;
            selfRect = GetComponent<RectTransform>();
            EnsureCursorDot();
        }

        private void Awake()
        {
            selfRect = GetComponent<RectTransform>();
        }

        private void OnDisable()
        {
            if (builtInPressingViewport && window != null)
            {
                window.SendPointer(AndroidMotionActions.Cancel, lastUv);
            }
            if (builtInHoveringViewport && window != null)
            {
                window.SendHover(AndroidMotionActions.HoverExit, lastUv);
            }
            builtInPressingViewport = false;
            builtInHoveringViewport = false;
            xrHandPinchLatched = false;
            EndBoardGrab(buzzRelease: false, isLeftSide: false, xrDevice: default);
            ClearHoveredPill();
            if (cursorDotRect != null) cursorDotRect.gameObject.SetActive(false);

            StopAllHaptics();
        }

        private void Update()
        {
            StepPulsedHaptics();

            if (!enableBuiltInRayInput || window == null || viewportTransform == null)
                return;

            float fingertipNearness = MeasureFingertipNearness();

            if (Time.frameCount - lastEventSystemFrame <= 2)
            {
                window.NotifySpatialInteractionState(Mathf.Max(fingertipNearness, 0.6f), false);
                if (cursorDotRect != null) cursorDotRect.gameObject.SetActive(false);
                return;
            }

            if (!TryGetActiveRay(
                    out Ray ray,
                    out bool isPressed,
                    out bool pressedThisFrame,
                    out bool releasedThisFrame,
                    out Vector2 scrollDelta,
                    out bool isControllerSource,
                    out bool isLeftSide,
                    out UnityEngine.XR.InputDevice activeXrDevice))
            {
                EndBoardGrab(buzzRelease: false, isLeftSide: false, xrDevice: default);
                ClearBuiltInViewportState();
                ClearHoveredPill();
                window.NotifySpatialInteractionState(fingertipNearness, false);
                return;
            }

            // 1. Continue smooth 0.32s SmoothDamp board dragging while holding the bottom grab pill.
            if (draggingBoardHandle)
            {
                if (releasedThisFrame || !isPressed)
                {
                    EndBoardGrab(buzzRelease: isControllerSource, isLeftSide: isLeftSide, xrDevice: activeXrDevice);
                }
                else
                {
                    if (Mathf.Abs(scrollDelta.y) > 0.01f)
                    {
                        grabDistanceMeters = Mathf.Clamp(grabDistanceMeters + scrollDelta.y * 0.03f, 0.45f, 3.2f);
                    }
                    Vector3 targetPos = ray.GetPoint(grabDistanceMeters) + grabLocalOffset;
                    Transform root = window.transform;
                    root.position = Vector3.SmoothDamp(root.position, targetPos, ref boardMoveVelocity, 0.32f);

                    Camera eye = Camera.main;
                    if (eye != null)
                    {
                        Vector3 look = root.position - eye.transform.position;
                        look.y = 0f;
                        if (look.sqrMagnitude > 0.0001f)
                        {
                            Quaternion targetRot = Quaternion.LookRotation(look.normalized, Vector3.up);
                            root.rotation = Quaternion.Slerp(root.rotation, targetRot, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
                        }
                    }

                    window.NotifySpatialInteractionState(1f, true);
                    if (cursorDotRect != null) cursorDotRect.gameObject.SetActive(false);
                    return;
                }
            }

            // 2. Check bottom dock & grab-handle pills (forgiving >= 60dp hit regions).
            if (dockRect != null && dockRect.gameObject.activeInHierarchy &&
                TryHitDockPill(ray, out SpatialPillControl hitPill, out bool hitIsGrabHandle, out Vector3 dockHitWorld))
            {
                ClearBuiltInViewportHover();
                PositionCursor(dockHitWorld);
                SetHoveredPill(hitPill);
                window.NotifySpatialInteractionState(1f, false);

                if (pressedThisFrame && hitPill != null && hitPill.Interactable)
                {
                    pressedPill = hitPill;
                    pressedPill.SetPressedExternal(true);

                    if (hitIsGrabHandle)
                    {
                        draggingBoardHandle = true;
                        grabDistanceMeters = Mathf.Clamp(Vector3.Distance(ray.origin, dockHitWorld), 0.45f, 3.2f);
                        grabLocalOffset = window.transform.position - dockHitWorld;
                        boardMoveVelocity = Vector3.zero;
                        TriggerHapticBuzz(isControllerSource, isLeftSide, activeXrDevice, WebViewSpatialMotion.GrabBuzzAmplitude, WebViewSpatialMotion.GrabBuzzSeconds);
                    }
                    else
                    {
                        TriggerHapticBuzz(isControllerSource, isLeftSide, activeXrDevice, WebViewSpatialMotion.TapBuzzAmplitude, WebViewSpatialMotion.TapBuzzSeconds);
                    }
                }
                else if (releasedThisFrame && pressedPill != null)
                {
                    SpatialPillControl completed = pressedPill;
                    pressedPill.SetPressedExternal(false);
                    pressedPill = null;
                    if (completed == hitPill && !hitIsGrabHandle)
                    {
                        completed.TriggerClickExternal();
                    }
                }
                return;
            }

            ClearHoveredPill();

            // 3. Check browser viewport collider (viewport stays completely still while aimed at!).
            if (viewportCollider != null &&
                viewportCollider.Raycast(ray, out RaycastHit hit, maxRayDistance))
            {
                Vector2 uv = WorldPointToViewportUv(viewportTransform, hit.point);
                lastUv = uv;
                PositionCursor(hit.point);
                window.NotifySpatialInteractionState(1f, false);

                if (!builtInHoveringViewport && !isPressed)
                {
                    builtInHoveringViewport = true;
                    window.SendHover(AndroidMotionActions.HoverEnter, uv);
                }

                if (pressedThisFrame)
                {
                    builtInPressingViewport = true;
                    window.SendPointer(AndroidMotionActions.Down, uv);
                    TriggerHapticBuzz(isControllerSource, isLeftSide, activeXrDevice, WebViewSpatialMotion.TapBuzzAmplitude, WebViewSpatialMotion.TapBuzzSeconds);
                }
                else if (builtInPressingViewport && isPressed)
                {
                    window.SendPointer(AndroidMotionActions.Move, uv);
                }
                else if (builtInPressingViewport && (releasedThisFrame || !isPressed))
                {
                    builtInPressingViewport = false;
                    window.SendPointer(AndroidMotionActions.Up, uv);
                }
                else if (!builtInPressingViewport)
                {
                    window.SendHover(AndroidMotionActions.HoverMove, uv);
                }

                if (scrollDelta.sqrMagnitude > 0.0001f)
                {
                    window.SendScroll(uv, scrollDelta);
                }
            }
            else
            {
                if (builtInPressingViewport && (releasedThisFrame || !isPressed))
                {
                    builtInPressingViewport = false;
                    window.SendPointer(AndroidMotionActions.Up, lastUv);
                }
                ClearBuiltInViewportHover();
                window.NotifySpatialInteractionState(fingertipNearness, false);
                if (cursorDotRect != null) cursorDotRect.gameObject.SetActive(false);
            }
        }

        public static Vector2 WorldPointToViewportUv(Transform viewport, Vector3 worldPoint)
        {
            Vector3 local = viewport.InverseTransformPoint(worldPoint);
            float u = Mathf.Clamp01(local.x + 0.5f);
            float v = Mathf.Clamp01(0.5f - local.y);
            return new Vector2(u, v);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null || !TryEventToUv(eventData, out Vector2 uv)) return;
            lastUv = uv;
            window.NotifySpatialInteractionState(1f, false);
            window.SendHover(AndroidMotionActions.HoverEnter, uv);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null) return;
            window.NotifySpatialInteractionState(0f, false);
            window.SendHover(AndroidMotionActions.HoverExit, lastUv);
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null || eventData.dragging || !TryEventToUv(eventData, out Vector2 uv)) return;
            lastUv = uv;
            window.SendHover(AndroidMotionActions.HoverMove, uv);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null || !TryEventToUv(eventData, out Vector2 uv)) return;
            lastUv = uv;
            window.SendPointer(AndroidMotionActions.Down, uv);
        }

        public void OnDrag(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null || !TryEventToUv(eventData, out Vector2 uv)) return;
            lastUv = uv;
            window.SendPointer(AndroidMotionActions.Move, uv);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null) return;
            if (TryEventToUv(eventData, out Vector2 uv)) lastUv = uv;
            window.SendPointer(AndroidMotionActions.Up, lastUv);
        }

        public void OnScroll(PointerEventData eventData)
        {
            lastEventSystemFrame = Time.frameCount;
            if (window == null) return;
            if (TryEventToUv(eventData, out Vector2 uv)) lastUv = uv;
            window.SendScroll(lastUv, eventData.scrollDelta);
        }

        private float MeasureFingertipNearness()
        {
            if (viewportCollider == null) return 0f;
            float best = 0f;

#if SPATIAL_WEBVIEW_META_OVR && !UNITY_EDITOR
            RefreshMetaRigCache();
            if (cachedHands != null)
            {
                for (int i = 0; i < cachedHands.Length; i++)
                {
                    OVRHand hand = cachedHands[i];
                    if (hand == null || !hand.isActiveAndEnabled || !hand.IsTracked) continue;
                    best = Mathf.Max(best, DistanceToNearness(hand.transform.position));
                }
            }
#endif

#if SPATIAL_WEBVIEW_XR_HANDS && !UNITY_EDITOR
            SubsystemManager.GetSubsystems(s_HandSubsystems);
            Pose originPose = GetXrOriginPose();
            for (int i = 0; i < s_HandSubsystems.Count; i++)
            {
                XRHandSubsystem sub = s_HandSubsystems[i];
                if (sub == null || !sub.running) continue;
                if (sub.rightHand.isTracked && sub.rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose rp))
                {
                    best = Mathf.Max(best, DistanceToNearness(rp.GetTransformedBy(originPose).position));
                }
                if (sub.leftHand.isTracked && sub.leftHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose lp))
                {
                    best = Mathf.Max(best, DistanceToNearness(lp.GetTransformedBy(originPose).position));
                }
            }
#endif
            return best;
        }

        private float DistanceToNearness(Vector3 worldPos)
        {
            Vector3 closest = viewportCollider.ClosestPoint(worldPos);
            float dist = Vector3.Distance(worldPos, closest);
            return dist < WebViewSpatialMotion.ApproachRangeMeters
                ? 1f - (dist / WebViewSpatialMotion.ApproachRangeMeters)
                : 0f;
        }

        private bool TryEventToUv(PointerEventData eventData, out Vector2 uv)
        {
            uv = lastUv;
            if (eventData.pointerCurrentRaycast.isValid && viewportTransform != null)
            {
                uv = WorldPointToViewportUv(viewportTransform, eventData.pointerCurrentRaycast.worldPosition);
                return true;
            }

            if (selfRect != null &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    selfRect,
                    eventData.position,
                    eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera,
                    out Vector2 localPoint))
            {
                Rect r = selfRect.rect;
                if (r.width > 0.0001f && r.height > 0.0001f)
                {
                    float u = Mathf.Clamp01((localPoint.x - r.xMin) / r.width);
                    float v = Mathf.Clamp01(1f - ((localPoint.y - r.yMin) / r.height));
                    uv = new Vector2(u, v);
                    return true;
                }
            }

            return false;
        }

        private bool TryGetActiveRay(
            out Ray ray,
            out bool isPressed,
            out bool pressedThisFrame,
            out bool releasedThisFrame,
            out Vector2 scrollDelta,
            out bool isControllerSource,
            out bool isLeftSide,
            out UnityEngine.XR.InputDevice activeXrDevice)
        {
            ray = default;
            isPressed = false;
            pressedThisFrame = false;
            releasedThisFrame = false;
            scrollDelta = Vector2.zero;
            isControllerSource = false;
            isLeftSide = false;
            activeXrDevice = default;

            bool wasHeld = builtInPressingViewport || draggingBoardHandle || pressedPill != null;

#if SPATIAL_WEBVIEW_META_OVR && !UNITY_EDITOR
            RefreshMetaRigCache();

            // 1. Meta OVRHand index pinch ray.
            if (cachedHands != null)
            {
                for (int i = 0; i < cachedHands.Length; i++)
                {
                    OVRHand hand = cachedHands[i];
                    if (hand == null || !hand.isActiveAndEnabled || !hand.IsTracked || !hand.IsPointerPoseValid)
                        continue;

                    Transform pose = hand.PointerPose;
                    ray = new Ray(pose.position, pose.forward);
                    bool pinching = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                    pressedThisFrame = pinching && !wasHeld;
                    releasedThisFrame = !pinching && wasHeld;
                    isPressed = pinching;
                    return true;
                }
            }

            // 2. Meta Touch controller ray + thumbstick scroll.
            if (cachedCameraRig != null)
            {
                isLeftSide =
                    OVRInput.GetActiveController() == OVRInput.Controller.LTouch &&
                    cachedCameraRig.leftHandAnchor != null;
                Transform anchor = isLeftSide ? cachedCameraRig.leftHandAnchor : cachedCameraRig.rightHandAnchor;
                OVRInput.Controller ctrl = isLeftSide ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

                if (anchor != null && (OVRInput.IsControllerConnected(ctrl) || OVRInput.IsControllerConnected(OVRInput.Controller.Touch)))
                {
                    ray = new Ray(anchor.position, anchor.forward);
                    isPressed = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, ctrl) ||
                                OVRInput.Get(OVRInput.Button.One, ctrl) ||
                                OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, ctrl);
                    pressedThisFrame = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, ctrl) ||
                                       OVRInput.GetDown(OVRInput.Button.One, ctrl) ||
                                       OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, ctrl);
                    releasedThisFrame = OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, ctrl) ||
                                        OVRInput.GetUp(OVRInput.Button.One, ctrl) ||
                                        OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, ctrl);

                    Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, ctrl);
                    if (stick.sqrMagnitude > 0.04f)
                    {
                        scrollDelta = new Vector2(stick.x, stick.y) * (thumbstickScrollSpeed * Time.deltaTime * 12f);
                    }
                    isControllerSource = true;
                    return true;
                }
            }
#endif

#if SPATIAL_WEBVIEW_XR_HANDS && !UNITY_EDITOR
            // 3. Cross-vendor OpenXR Hand Tracking (Android XR, Pico, Vive, Quest OpenXR).
            if (TryGetOpenXrHandRay(wasHeld, out ray, out isPressed, out pressedThisFrame, out releasedThisFrame))
            {
                return true;
            }
#endif

#if !UNITY_EDITOR
            // 4. Cross-vendor OpenXR 6DoF Controller (Pico, Android XR, Vive, Snapdragon Spaces).
            if (TryGetGenericXrControllerRay(
                    wasHeld,
                    out ray,
                    out isPressed,
                    out pressedThisFrame,
                    out releasedThisFrame,
                    out scrollDelta,
                    out isLeftSide,
                    out activeXrDevice))
            {
                isControllerSource = true;
                return true;
            }
#endif

#if SPATIAL_WEBVIEW_INPUT_SYSTEM
            Camera cam = Camera.main;
            if (cam != null)
            {
                // 5. Desktop Editor / Companion Mouse.
                if (Mouse.current != null && (Application.isEditor || !UnityEngine.XR.XRSettings.isDeviceActive))
                {
                    Vector2 mousePos = Mouse.current.position.ReadValue();
                    ray = cam.ScreenPointToRay(mousePos);
                    isPressed = Mouse.current.leftButton.isPressed;
                    pressedThisFrame = Mouse.current.leftButton.wasPressedThisFrame;
                    releasedThisFrame = Mouse.current.leftButton.wasReleasedThisFrame;
                    Vector2 wheel = Mouse.current.scroll.ReadValue();
                    if (wheel.sqrMagnitude > 0.01f)
                    {
                        scrollDelta = wheel * 0.02f;
                    }
                    return true;
                }

                // 6. Controllerless VR/AR Smart Glasses Fallback (XREAL, Viture, RayNeo):
                // Head gaze + phone screen tap, Bluetooth ring/gamepad button, or spacebar.
                if (enableGlassesGazeFallback)
                {
                    ray = new Ray(cam.transform.position, cam.transform.forward);
                    bool tapHeld =
                        (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed) ||
                        (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed) ||
                        (Keyboard.current != null && Keyboard.current.spaceKey.isPressed);
                    pressedThisFrame = tapHeld && !wasHeld;
                    releasedThisFrame = !tapHeld && wasHeld;
                    isPressed = tapHeld;

                    if (Gamepad.current != null)
                    {
                        Vector2 stick = Gamepad.current.rightStick.ReadValue();
                        if (stick.sqrMagnitude > 0.04f)
                        {
                            scrollDelta = stick * (thumbstickScrollSpeed * Time.deltaTime * 12f);
                        }
                    }
                    return true;
                }
            }
#endif
            return false;
        }

#if SPATIAL_WEBVIEW_XR_HANDS
        private bool TryGetOpenXrHandRay(
            bool wasHeld,
            out Ray ray,
            out bool isPressed,
            out bool pressedThisFrame,
            out bool releasedThisFrame)
        {
            ray = default;
            isPressed = false;
            pressedThisFrame = false;
            releasedThisFrame = false;

            SubsystemManager.GetSubsystems(s_HandSubsystems);
            Camera eye = Camera.main;
            if (eye == null) return false;
            Pose originPose = GetXrOriginPose();

            for (int i = 0; i < s_HandSubsystems.Count; i++)
            {
                XRHandSubsystem sub = s_HandSubsystems[i];
                if (sub == null || !sub.running) continue;

                if (TryExtractSingleHandPinchRay(sub.rightHand, originPose, eye.transform.position, out ray, out float pinchDist) ||
                    TryExtractSingleHandPinchRay(sub.leftHand, originPose, eye.transform.position, out ray, out pinchDist))
                {
                    if (!xrHandPinchLatched && pinchDist <= PinchDownMeters)
                    {
                        xrHandPinchLatched = true;
                    }
                    else if (xrHandPinchLatched && pinchDist >= PinchReleaseMeters)
                    {
                        xrHandPinchLatched = false;
                    }

                    isPressed = xrHandPinchLatched;
                    pressedThisFrame = isPressed && !wasHeld;
                    releasedThisFrame = !isPressed && wasHeld;
                    return true;
                }
            }

            xrHandPinchLatched = false;
            return false;
        }

        private static bool TryExtractSingleHandPinchRay(
            XRHand hand,
            Pose originPose,
            Vector3 eyePos,
            out Ray ray,
            out float pinchDistance)
        {
            ray = default;
            pinchDistance = 1f;
            if (!hand.isTracked) return false;

            if (!hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexLocal) ||
                !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbLocal))
            {
                return false;
            }

            Vector3 indexWorld = indexLocal.GetTransformedBy(originPose).position;
            Vector3 thumbWorld = thumbLocal.GetTransformedBy(originPose).position;
            Vector3 midpoint = (indexWorld + thumbWorld) * 0.5f;
            pinchDistance = Vector3.Distance(indexWorld, thumbWorld);

            Vector3 shoulderOrigin = eyePos + Vector3.down * 0.14f;
            Vector3 dir = (midpoint - shoulderOrigin).normalized;
            if (dir.sqrMagnitude < 0.001f) return false;

            ray = new Ray(midpoint, dir);
            return true;
        }
#endif

        private bool TryGetGenericXrControllerRay(
            bool wasHeld,
            out Ray ray,
            out bool isPressed,
            out bool pressedThisFrame,
            out bool releasedThisFrame,
            out Vector2 scrollDelta,
            out bool isLeftSide,
            out UnityEngine.XR.InputDevice matchedDevice)
        {
            ray = default;
            isPressed = false;
            pressedThisFrame = false;
            releasedThisFrame = false;
            scrollDelta = Vector2.zero;
            isLeftSide = false;
            matchedDevice = default;

            xrControllers.Clear();
            UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                UnityEngine.XR.InputDeviceCharacteristics.Controller | UnityEngine.XR.InputDeviceCharacteristics.HeldInHand,
                xrControllers);

            Pose originPose = GetXrOriginPose();
            for (int i = 0; i < xrControllers.Count; i++)
            {
                UnityEngine.XR.InputDevice dev = xrControllers[i];
                if (!dev.isValid) continue;
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && !tracked)
                    continue;

                if (!dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 localPos) ||
                    !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion localRot))
                {
                    continue;
                }

                Pose worldPose = new Pose(localPos, localRot).GetTransformedBy(originPose);
                ray = new Ray(worldPose.position, worldPose.forward);

                bool trigger = dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool t) && t;
                bool primary = dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool p) && p;
                bool grip = dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool g) && g;
                isPressed = trigger || primary || grip;
                pressedThisFrame = isPressed && !wasHeld;
                releasedThisFrame = !isPressed && wasHeld;

                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 stick) &&
                    stick.sqrMagnitude > 0.04f)
                {
                    scrollDelta = stick * (thumbstickScrollSpeed * Time.deltaTime * 12f);
                }

                isLeftSide = (dev.characteristics & UnityEngine.XR.InputDeviceCharacteristics.Left) != 0;
                matchedDevice = dev;
                return true;
            }

            return false;
        }

        private static Pose GetXrOriginPose()
        {
            Camera cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
            {
                Transform origin = cam.transform.parent;
                return new Pose(origin.position, origin.rotation);
            }
            return Pose.identity;
        }

#if SPATIAL_WEBVIEW_META_OVR
        private void RefreshMetaRigCache()
        {
            if (Time.unscaledTime < nextRigLookupTime && cachedCameraRig != null)
                return;

            nextRigLookupTime = Time.unscaledTime + 2.0f;
            if (cachedCameraRig == null)
            {
                cachedCameraRig = FindFirstObjectByType<OVRCameraRig>();
            }
            if (cachedHands == null || cachedHands.Length == 0)
            {
                cachedHands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            }
        }
#endif

        private void TriggerHapticBuzz(
            bool isController,
            bool isLeft,
            UnityEngine.XR.InputDevice xrDevice,
            float amplitude,
            float seconds)
        {
#if !UNITY_EDITOR
            if (!isController) return;

#if SPATIAL_WEBVIEW_META_OVR
            OVRInput.Controller target = isLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            if ((OVRInput.GetActiveController() & target) != 0)
            {
                buzzingOvrController = target;
                buzzAmplitude = amplitude;
                buzzUntilTime = Time.unscaledTime + seconds;
                return;
            }
#endif
            if (xrDevice.isValid)
            {
                buzzingXrDevice = xrDevice;
                xrBuzzActive = true;
                buzzAmplitude = amplitude;
                buzzUntilTime = Time.unscaledTime + seconds;
            }
#endif
        }

        private void StepPulsedHaptics()
        {
#if !UNITY_EDITOR
            bool active = Time.unscaledTime < buzzUntilTime;
            bool pulseFrame = active && (buzzFrameCounter++ % 3 == 0);

#if SPATIAL_WEBVIEW_META_OVR
            if (buzzingOvrController != OVRInput.Controller.None)
            {
                OVRInput.SetControllerVibration(1f, pulseFrame ? buzzAmplitude : 0f, buzzingOvrController);
                if (!active)
                {
                    OVRInput.SetControllerVibration(0f, 0f, buzzingOvrController);
                    buzzingOvrController = OVRInput.Controller.None;
                }
            }
#endif
            if (xrBuzzActive)
            {
                if (active && pulseFrame && buzzingXrDevice.isValid)
                {
                    buzzingXrDevice.SendHapticImpulse(0u, buzzAmplitude, Mathf.Max(0.015f, Time.unscaledDeltaTime));
                }
                else if (!active)
                {
                    if (buzzingXrDevice.isValid) buzzingXrDevice.StopHaptics();
                    xrBuzzActive = false;
                }
            }
#endif
        }

        private void StopAllHaptics()
        {
#if SPATIAL_WEBVIEW_META_OVR
            if (buzzingOvrController != OVRInput.Controller.None)
            {
                OVRInput.SetControllerVibration(0f, 0f, buzzingOvrController);
                buzzingOvrController = OVRInput.Controller.None;
            }
#endif
            if (xrBuzzActive && buzzingXrDevice.isValid)
            {
                buzzingXrDevice.StopHaptics();
            }
            xrBuzzActive = false;
        }

        private bool TryHitDockPill(
            Ray ray,
            out SpatialPillControl hitPill,
            out bool hitIsGrabHandle,
            out Vector3 worldHit)
        {
            hitPill = null;
            hitIsGrabHandle = false;
            worldHit = Vector3.zero;

            Plane plane = new Plane(-dockRect.forward, dockRect.position);
            if (!plane.Raycast(ray, out float enter) || enter <= 0f || enter > maxRayDistance)
                return false;

            worldHit = ray.GetPoint(enter);
            Vector3 local = dockRect.InverseTransformPoint(worldHit);
            Rect padded = dockRect.rect;
            padded.xMin -= 16f;
            padded.xMax += 16f;
            padded.yMin -= 14f;
            padded.yMax += 14f;
            if (!padded.Contains(new Vector2(local.x, local.y)))
                return false;

            SpatialPillControl[] pills = dockRect.GetComponentsInChildren<SpatialPillControl>(false);
            for (int i = 0; i < pills.Length; i++)
            {
                RectTransform pr = pills[i].GetComponent<RectTransform>();
                if (pr == null) continue;
                Vector3 pl = pr.InverseTransformPoint(worldHit);
                Rect r = pr.rect;
                r.xMin -= 8f;
                r.xMax += 8f;
                r.yMin -= 8f;
                r.yMax += 8f;
                if (r.Contains(new Vector2(pl.x, pl.y)))
                {
                    hitPill = pills[i];
                    hitIsGrabHandle = grabHandleRect != null && pr == grabHandleRect;
                    return true;
                }
            }

            return true;
        }

        private void SetHoveredPill(SpatialPillControl pill)
        {
            if (hoveredPill == pill) return;
            if (hoveredPill != null) hoveredPill.SetHoveredExternal(false);
            hoveredPill = pill;
            if (hoveredPill != null) hoveredPill.SetHoveredExternal(true);
        }

        private void ClearHoveredPill()
        {
            if (pressedPill != null)
            {
                pressedPill.SetPressedExternal(false);
                pressedPill = null;
            }
            if (hoveredPill != null)
            {
                hoveredPill.SetHoveredExternal(false);
                hoveredPill = null;
            }
        }

        private void EndBoardGrab(bool buzzRelease, bool isLeftSide, UnityEngine.XR.InputDevice xrDevice)
        {
            if (!draggingBoardHandle) return;
            draggingBoardHandle = false;
            if (pressedPill != null)
            {
                pressedPill.SetPressedExternal(false);
                pressedPill = null;
            }
            window?.NotifySpatialInteractionState(1f, false);
            if (buzzRelease)
            {
                TriggerHapticBuzz(true, isLeftSide, xrDevice, WebViewSpatialMotion.ReleaseBuzzAmplitude, WebViewSpatialMotion.ReleaseBuzzSeconds);
            }
        }

        private void EnsureCursorDot()
        {
            if (cursorDotRect != null || selfRect == null) return;
            var dotGo = new GameObject("SpatialWebViewCursorDot", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(selfRect.parent != null ? selfRect.parent : selfRect, false);

            Image img = dotGo.GetComponent<Image>();
            SpatialPillControl.ApplyPillRounding(img, new Vector2(14f, 14f));
            img.color = new Color(0.88f, 0.94f, 1.0f, 0.92f);
            img.raycastTarget = false;

            cursorDotRect = dotGo.GetComponent<RectTransform>();
            cursorDotRect.sizeDelta = new Vector2(14f, 14f);
            dotGo.SetActive(false);
        }

        private void PositionCursor(Vector3 worldHit)
        {
            if (cursorDotRect == null || cursorDotRect.parent == null) return;
            if (!cursorDotRect.gameObject.activeSelf) cursorDotRect.gameObject.SetActive(true);
            Vector3 localInCanvas = cursorDotRect.parent.InverseTransformPoint(worldHit);
            cursorDotRect.localPosition = new Vector3(localInCanvas.x, localInCanvas.y, -2f);
        }

        private void ClearBuiltInViewportHover()
        {
            if (!builtInHoveringViewport || window == null) return;
            builtInHoveringViewport = false;
            window.SendHover(AndroidMotionActions.HoverExit, lastUv);
        }

        private void ClearBuiltInViewportState()
        {
            if (builtInPressingViewport && window != null)
            {
                builtInPressingViewport = false;
                window.SendPointer(AndroidMotionActions.Cancel, lastUv);
            }
            ClearBuiltInViewportHover();
            if (cursorDotRect != null) cursorDotRect.gameObject.SetActive(false);
        }
    }
}
