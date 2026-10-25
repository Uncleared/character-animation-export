using System;
using UnityEditor;
using UnityEngine;

namespace CharacterAnimationExport
{
    public sealed class AnimationExportPreview : IDisposable
    {
        readonly PreviewRenderUtility preview;
        readonly GameObject model;
        readonly AnimationClip clip;

        public AnimationExportPreview(GameObject sourceModel, AnimationClip animation)
        {
            preview = new PreviewRenderUtility();
            preview.cameraFieldOfView = 30;
            preview.camera.nearClipPlane = 0.001f;
            preview.camera.farClipPlane = 10000;
            preview.camera.stereoTargetEye = StereoTargetEyeMask.None;
            model = preview.InstantiatePrefabInScene(sourceModel);
            clip = animation;
            preview.lights[0].intensity = 1.1f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35, 30, 0);
            preview.lights[1].intensity = 0.8f;
            preview.lights[1].transform.rotation = Quaternion.Euler(330, 210, 0);
        }

        public void Draw(Rect rect, float time)
        {
            if (model == null || clip == null || rect.width <= 0 || rect.height <= 0) return;
            clip.SampleAnimation(model, Mathf.Clamp(time, 0, clip.length));
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            var bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.zero, Vector3.one);
            for (var index = 1; index < renderers.Length; index++) bounds.Encapsulate(renderers[index].bounds);
            var radius = Mathf.Max(0.05f, bounds.extents.magnitude);
            var distance = radius / Mathf.Sin(preview.cameraFieldOfView * Mathf.Deg2Rad * 0.5f) * 1.2f;
            preview.camera.transform.position = bounds.center + new Vector3(0, radius * 0.1f, -distance);
            preview.camera.transform.LookAt(bounds.center);
            preview.BeginPreview(rect, GUIStyle.none);
            preview.Render(true);
            var texture = preview.EndPreview();
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
        }

        public void Dispose() => preview.Cleanup();
    }
}
