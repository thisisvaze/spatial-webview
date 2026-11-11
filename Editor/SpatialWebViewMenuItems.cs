using UnityEditor;
using UnityEngine;

namespace SpatialWebView.Editor
{
    public static class SpatialWebViewMenuItems
    {
        [MenuItem("GameObject/XR/Spatial WebView", false, 10)]
        private static void CreateBrowserWindow(MenuCommand menuCommand)
        {
            var go = new GameObject("SpatialWebView");
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);

            Camera cam = Camera.main;
            if (cam != null && menuCommand.context == null)
            {
                Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                go.transform.position = cam.transform.position + forward * 1.45f;
                go.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            else if (menuCommand.context == null)
            {
                go.transform.position = new Vector3(0f, 1.4f, 1.45f);
            }

            go.AddComponent<SpatialWebViewWindow>();
            Undo.RegisterCreatedObjectUndo(go, "Create Spatial WebView");
            Selection.activeGameObject = go;
        }
    }
}
