﻿using Mirror;
using Smooth;
using UnityEngine;
using Vuplex.WebView;

public class AutoBrowser : Browser
{
    public float deskHeight = 0.724f;
    public Vector3 _belowTableLocalPosition;
    public Vector3 _defaultLocalPosition;
    public Transform webViewParent;
    public BoxCollider screenCollider;

    protected override void Start()
    {
        base.Start();
        BonsaiLog("Start");

        _defaultLocalPosition = transform.localPosition;
        _belowTableLocalPosition = _defaultLocalPosition;
        //_belowTableLocalPosition.y = -Bounds.y;

        _belowTableLocalPosition.y = transform.InverseTransformPoint(0, deskHeight, 0).y - Bounds.y / 2 - 0.001f;

        ListenersReady += NavHome;
    }

    private void NavHome()
    {
        PostMessage(BrowserMessage.NavHome);
    }

    public Vector2Int ChangeAspect(Vector2 newAspect)
    {
        var aspectRatio = newAspect.x / newAspect.y;
        var localScale = new Vector3(Bounds.y * aspectRatio, Bounds.y, 1);
        if (localScale.x > Bounds.x)
        {
            localScale = new Vector3(Bounds.x, Bounds.x * (1f / aspectRatio), 1);
        }

        var resolution = AutoResolution(localScale, distanceEstimate, pixelPerDegree, newAspect);

        if (!Mathf.Approximately(1, WebViewPrefab.WebView.Resolution))
        {
            WebViewPrefab.WebView.SetResolution(1);
        }

        WebViewPrefab.WebView.Resize(resolution.x, resolution.y);

        BonsaiLog($"ChangeAspect {resolution}");

        boundsTransform.localScale = localScale;

#if UNITY_ANDROID && !UNITY_EDITOR
        RebuildOverlay(resolution);
#endif

        return resolution;
    }

    public void SetHeight(float t)
    {
        var heightT = t;
        screenCollider.enabled = !Mathf.Approximately(t, 0);
        transform.localPosition = Vector3.Lerp(_belowTableLocalPosition, _defaultLocalPosition, Mathf.Clamp01(heightT));
    }

    protected override void SetupWebViewPrefab()
    {
        var material = new Material(Resources.Load<Material>("OnTopViewportClipped"));
        material.SetFloat("_ClipLevel", deskHeight);
        WebViewPrefab = WebViewPrefabCustom.Instantiate(1, 1, material);

#if UNITY_ANDROID && !UNITY_EDITOR
        holePuncherMaterial = new Material(Resources.Load<Material>("OnTopUnderlayClipped"));
        holePuncherMaterial.SetFloat("_ClipLevel", deskHeight);
        WebViewPrefab = WebViewPrefabCustom.Instantiate(1, 1);
#endif

        Destroy(WebViewPrefab.Collider);
        WebViewPrefab.transform.SetParent(webViewParent, false);

        Resizer = WebViewPrefab.transform.Find("WebViewPrefabResizer");
        WebViewView = Resizer.transform.Find("WebViewPrefabView");


        //WebViewPrefab.transform.localPosition = new Vector3(0, 0.5f, 0);
        //WebViewPrefab.transform.localEulerAngles = new Vector3(0, 180, 0);

#if UNITY_ANDROID && !UNITY_EDITOR
        WebViewView.GetComponent<MeshRenderer>().enabled = false;
#endif

        WebViewPrefab.Initialized += (sender, eventArgs) =>
        {
            ChangeAspect(startingAspect);
            WebViewView.SetParent(boundsTransform, false);
            WebViewView.transform.localPosition = Vector3.zero;
            WebViewView.transform.localEulerAngles = Vector3.zero;
        };
        base.SetupWebViewPrefab();
    }

    private void BonsaiLog(string msg)
    {
        Debug.Log("<color=orange>BonsaiAutoBrowser: </color>: " + msg);
    }

    private void BonsaiLogWarning(string msg)
    {
        Debug.LogWarning("<color=orange>BonsaiAutoBrowser: </color>: " + msg);
    }

    private void BonsaiLogError(string msg)
    {
        Debug.LogError("<color=orange>BonsaiAutoBrowser: </color>: " + msg);
    }
}