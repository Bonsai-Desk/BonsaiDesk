﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using mixpanel;
using OVRSimpleJSON;
using UnityEngine;
using UnityEngine.Networking;
using VivoxUnity;
using Vuplex.WebView;

public class AutoBrowserController : NetworkBehaviour
{
    private const float ClientJoinGracePeriod = 10f;
    private const float ClientPingTolerance = 2f;
    private const float ClientPingInterval = 0.1f;
    private const float MaxReadyUpPeriod = 10f;
    private const float VideoSyncTolerance = 2f;
    public TogglePause togglePause;
    public VideoCubeSpot videoCubeSpot;
    private readonly Dictionary<uint, double> _clientsJoinedNetworkTime = new Dictionary<uint, double>();
    private readonly Dictionary<uint, double> _clientsLastPing = new Dictionary<uint, double>();
    private readonly Dictionary<uint, PlayerState> _clientsPlayerStatus = new Dictionary<uint, PlayerState>();
    private readonly float _setVolumeLevelEvery = 0.5f;
    private bool _allGood;
    public AutoBrowser _autoBrowser;
    private double _beginReadyUpTime;
    private double _clientLastSentPing;
    private float _clientPlayerDuration;
    private PlayerState _clientPlayerStatus;
    private float _clientPlayerTimeStamp;
    [SyncVar] private bool _contentActive;
    private double _contentInfoAtTime;
    private Coroutine _fetchAndReadyUp;

    [SyncVar] private float _height;
    [SyncVar] private ScrubData _idealScrub;
    private ContentInfo _serverContentInfo;
    private bool _serverVideoEnded;
    private float _setVolumeLevelLast;
    [SyncVar] private float _volumeLevel = 0.125f;
    private const float VolumeMax = 0.25f;

    private void Start()
    {
        // so the server runs a browser but does not sync it yet
        // it will need to be synced for streamer mode
        if (_autoBrowser.Initialized)
        {
            SetupBrowser();
        }
        else
        {
            _autoBrowser.BrowserReady += (_, e) =>
            {
                SetupBrowser();
            };
        }
    }

    private void Update()
    {
        if (isServer)
        {
            HandlePlayerServer();
            HandleScreenServer();
        }

        if (isClient)
        {
            HandlePlayerClient();
            HandleScreenClient();
        }
    }

    private void ResetClientPlayer()
    {
        Mixpanel.Track("Video Pause or Stop");
        _autoBrowser.PostMessage(YouTubeMessage.NavHome);
        _clientPlayerStatus = PlayerState.Unstarted;
    }

    public override void OnStartServer()
    {
        TLog("OnStartServer");
        base.OnStartServer();

        _clientsJoinedNetworkTime.Clear();
        _clientsLastPing.Clear();
        _clientsPlayerStatus.Clear();

        _serverContentInfo = new ContentInfo(false, "", new Vector2(1, 1));
        _contentActive = false;

        NetworkManagerGame.Singleton.ServerAddPlayer -= HandleServerAddPlayer;
        NetworkManagerGame.ServerDisconnect -= HandleServerDisconnect;
        togglePause.CmdSetPausedServer -= HandleCmdSetPausedServer;
        TableBrowserMenu.Singleton.SetVolumeLevel -= HandleSetVolumeLevel;
        TableBrowserMenu.Singleton.SeekPlayer -= HandleSeekPlayer;
        TableBrowserMenu.Singleton.RestartVideo -= HandleRestartVideo;

        NetworkManagerGame.Singleton.ServerAddPlayer += HandleServerAddPlayer;
        NetworkManagerGame.ServerDisconnect += HandleServerDisconnect;
        togglePause.CmdSetPausedServer += HandleCmdSetPausedServer;
        TableBrowserMenu.Singleton.SetVolumeLevel += HandleSetVolumeLevel;
        TableBrowserMenu.Singleton.SeekPlayer += HandleSeekPlayer;
        TableBrowserMenu.Singleton.RestartVideo += HandleRestartVideo;

        if (videoCubeSpot)
        {
            videoCubeSpot.SetNewVideo -= HandleSetNewVideo;
            videoCubeSpot.PlayVideo -= HandlePlayVideo;
            videoCubeSpot.StopVideo -= HandleStopVideo;

            videoCubeSpot.SetNewVideo += HandleSetNewVideo;
            videoCubeSpot.PlayVideo += HandlePlayVideo;
            videoCubeSpot.StopVideo += HandleStopVideo;
        }
        else
        {
            BonsaiLogError("No video cube spot.");
        }
    }

    private void HandleSetVolumeLevel(object sender, float level)
    {
        CmdSetVolumeLevel(level);
    }

    private void HandleRestartVideo(object sender, EventArgs e)
    {
        CmdReadyUp(0);
    }

    public override void OnStopServer()
    {
        TLog("On Stop Server");
        base.OnStopServer();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        _clientLastSentPing = Mathf.NegativeInfinity;
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        ResetClientPlayer();
    }

    [Server]
    private void HandleSetNewVideo(string id)
    {
        if (_serverContentInfo.Active)
        {
            CloseVideo();
        }
    }

    [Server]
    private void HandlePlayVideo(string id)
    {
        LoadVideo(id, 0);
    }

    [Server]
    private void HandleStopVideo()
    {
        if (_serverContentInfo.Active)
        {
            CloseVideo();
        }
    }

    [Server]
    private void HandleServerAddPlayer(NetworkConnection networkConnection, bool isLanOnly)
    {
        var newId = networkConnection.identity.netId;
        TLog($"AutoBrowserController add player [{newId}]");
        _clientsJoinedNetworkTime.Add(newId, NetworkTime.time);
        if (_serverContentInfo.Active)
        {
            BeginSync("new player joined");
            var timeStamp = _idealScrub.CurrentTimeStamp(NetworkTime.time);
            TargetReloadYoutube(networkConnection, _serverContentInfo.ID, timeStamp, _serverContentInfo.Aspect);
            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn.identity.netId != newId)
                {
                    TargetReadyUp(conn, timeStamp);
                }
            }
        }
    }

    [Server]
    private void FillClientsLastPing()
    {
        foreach (var conn in NetworkServer.connections.Values)
        {
            _clientsLastPing[conn.identity.netId] = NetworkTime.time;
        }
    }

    private void HandleServerDisconnect(object _, NetworkConnection conn)
    {
        var id = conn.identity.netId;
        TLog($"AutoBrowserController remove player [{id}]");
        _clientsJoinedNetworkTime.Remove(id);
        _clientsLastPing.Remove(id);
        _clientsPlayerStatus.Remove(id);
    }

    [Server]
    private void HandleCmdSetPausedServer(bool paused)
    {
        if (!_serverContentInfo.Active)
        {
            Debug.LogWarning("Ignoring attempt to toggle pause status when content is not active");
            return;
        }

        if (paused)
        {
            // todo set the toggle pause inactive now
            _idealScrub = _idealScrub.Pause(NetworkTime.time);
            var timeStamp = _idealScrub.CurrentTimeStamp(NetworkTime.time);
            TLog($"Paused scrub at timestamp {timeStamp}");
            RpcReadyUp(timeStamp);
        }
        else
        {
            // todo set the togglepause to activate when this starts
            var timeStamp = _idealScrub.CurrentTimeStamp(NetworkTime.time);
            BeginSync("toggled play");
            RpcReadyUp(timeStamp);
        }
    }

    private void HandlePlayerClient()
    {
        if (Time.time - _setVolumeLevelLast > _setVolumeLevelEvery)
        {
            _autoBrowser.PostMessage(YouTubeMessage.SetVolume(_volumeLevel));
            _setVolumeLevelLast = Time.time;
        }

        var clientReady = _clientPlayerStatus == PlayerState.Ready;
        var scrubStarted = _idealScrub.IsStarted(NetworkTime.time);
        //BonsaiLog($"{NetworkTime.time} {clientReady} {scrubStarted} {_clientPlayerStatus} {_idealScrub}");
        // post play message if paused/ready and player is behind ideal scrub
        if (clientReady && scrubStarted)
        {
            BonsaiLog("Issue Play");
            _autoBrowser.PostMessage(YouTubeMessage.Play);
        }

        // ping the server with the current timestamp
        // todo _contentInfo.Active is always false on client

        var shouldSendNewPing = NetworkTime.time - _clientLastSentPing > ClientPingInterval;
        var connectionGood = NetworkClient.connection != null;
        var identityGood = NetworkClient.connection.identity != null;
        if (_contentActive && shouldSendNewPing && connectionGood && identityGood)
        {
            var id = NetworkClient.connection.identity.netId;
            var now = NetworkTime.time;
            var timeStamp = _clientPlayerTimeStamp;
            CmdPingAndCheckTimeStamp(id, now, timeStamp);
            _clientLastSentPing = NetworkTime.time;
        }
    }

    private void HandleScreenClient()
    {
        _autoBrowser.SetHeight(_height);
    }

    private void BeginSync(string reason = "no reason provided")
    {
        TLog($"Beginning the sync process because [{reason}]");
        _serverVideoEnded = false;
        togglePause.SetInteractable(false);
        _allGood = false;
        _clientsLastPing.Clear();
        _clientsPlayerStatus.Clear();
        _beginReadyUpTime = NetworkTime.time;
        if (_idealScrub.Active)
        {
            _idealScrub = _idealScrub.Pause(NetworkTime.time);
        }
    }

    [Server]
    private void HandlePlayerServer()
    {
        if (_serverContentInfo.Active == false || _serverVideoEnded)
        {
            return;
        }

        if (_allGood && BadPingExists())
        {
            BeginSync("a bad ping");
            RpcReadyUp(_idealScrub.CurrentTimeStamp(NetworkTime.time));
        }

        if (!_allGood)
        {
            if (AllClientsReportPlayerStatus(PlayerState.Ready))
            {
                var networkTimeToUnpause = NetworkTime.time + 1;
                TLog($"All clients report ready, un-pausing the scrub at network time {networkTimeToUnpause}");
                if (!togglePause._paused)
                {
                    _idealScrub = _idealScrub.UnPauseAtNetworkTime(networkTimeToUnpause);
                }

                // todo this could become interactable at networkTimeToUnpause
                FillClientsLastPing();
                togglePause.SetInteractable(true);
                _allGood = true;
            }
            else if (NetworkTime.time - _beginReadyUpTime > MaxReadyUpPeriod)
            {
                var (numFailed, failedIdsStr) = FailedToMatchStatus(_clientsPlayerStatus, PlayerState.Ready);
                HardReload($"[{numFailed}/{NetworkServer.connections.Count}] clients failed to ready up [{failedIdsStr}]");
            }
        }
    }

    private static (int, string) FailedToMatchStatus(Dictionary<uint, PlayerState> _clientsPlayerStatus, PlayerState playerState)
    {
        var failedNetIds = new HashSet<string>();

        foreach (var info in _clientsPlayerStatus.Where(info => info.Value != playerState))
        {
            failedNetIds.Add($"{info.Key} {playerState}");
        }

        return (failedNetIds.Count, string.Join(", ", failedNetIds));
    }

    private bool BadPingExists()
    {
        var aBadPing = false;

        foreach (var entry in _clientsLastPing)
        {
            if (!ClientInGracePeriod(entry.Key) && !ClientPingedRecently(entry.Value))
            {
                BonsaiLog($"Client ({entry.Key}) bad last ping ({entry.Value}) @ ({NetworkTime.time})");
                aBadPing = true;
            }
        }

        return aBadPing;
    }

    [Server]
    private void HandleScreenServer()
    {
        const float transitionTime = 0.5f;
        var browserDown = !_serverContentInfo.Active || NetworkTime.time - _contentInfoAtTime < 1.5;
        var targetHeight = browserDown ? 0 : 1;

        if (!Mathf.Approximately(_height, targetHeight))
        {
            var easeFunction = browserDown ? CubicBezier.EaseOut : CubicBezier.EaseIn;
            var t = easeFunction.SampleInverse(_height);
            var step = 1f / transitionTime * Time.deltaTime;
            t = Mathf.MoveTowards(t, targetHeight, step);
            _height = easeFunction.Sample(t);
        }
    }

    private void SetupBrowser(bool restart = false)
    {
        BonsaiLog("SetupBrowser");
        if (!restart)
        {
            _autoBrowser.OnMessageEmitted(HandleJavascriptMessage);
        }
    }

    private void StopLoggingVideo()
    {
        Mixpanel.Track("Video Pause or Stop");
    }

    private void HandleJavascriptMessage(object _, EventArgs<string> eventArgs)
    {
        var json = JSONNode.Parse(eventArgs.Value) as JSONObject;

        if (json?["type"] != "infoCurrentTime")
        {
            TLog($"AB Received JSON {eventArgs.Value} at {NetworkTime.time}");
        }

        if (json?["current_time"] != null)
        {
            _clientPlayerTimeStamp = json["current_time"];
            _clientPlayerDuration = json["duration"];
        }

        switch (json?["type"].Value)
        {
            case "infoCurrentTime":
                return;
            case "error":
                Debug.LogError(Tag() + $"Javascript error [{json["error"].Value}]");
                return;
            case "playerError":
                BonsaiLog($"Player Error: {json["code"]}");
                var code = (int) json["code"];
                HandlePlayerError(code);
                break;
            case "stateChange":
                switch ((string) json["message"])
                {
                    case "READY":
                        if (_clientPlayerStatus != PlayerState.Ready)
                        {
                            StopLoggingVideo();
                        }
                        _clientPlayerStatus = PlayerState.Ready;
                        break;
                    case "PAUSED":
                        _clientPlayerStatus = PlayerState.Paused;
                        break;
                    case "PLAYING":
                        if (_clientPlayerStatus != PlayerState.Playing)
                        {
                            Mixpanel.StartTimedEvent("Video Pause or Stop");
                        }
                        _clientPlayerStatus = PlayerState.Playing;
                        break;
                    case "BUFFERING":
                        _clientPlayerStatus = PlayerState.Buffering;
                        break;
                    case "ENDED":
                        if (_clientPlayerStatus != PlayerState.Ended)
                        {
                            StopLoggingVideo();
                        }
                        CmdHandleVideoEnded(_clientPlayerTimeStamp);
                        _clientPlayerStatus = PlayerState.Ended;
                        break;
                    default:
                        BonsaiLogError("Unknown stateChange case: " + json["message"]);
                        break;
                }
                CmdUpdateClientPlayerStatus(NetworkClient.connection.identity.netId, _clientPlayerStatus);
                break;
        }
    }

    [Command (ignoreAuthority = true)]
    private void CmdEjectWithError(string text)
    {
        if (_serverContentInfo.Active)
        {
            StopLoggingVideo();
            RpcAddMessageToStack(text);
            videoCubeSpot.ServerEjectCurrentVideo();
        }
    }
    
    [ClientRpc]
    private void RpcAddMessageToStack(string text)
    {
        MessageStack.Singleton.AddMessage(text);
    }

    private void HandlePlayerError(int code)
    {
        switch (code)
        {
            case 2:
                CmdEjectWithError("Bad YouTube Id");
                break;
            case 5:
                CmdEjectWithError("Can't Be Played in HTML5 Player");
                break;
            case 101:
            case 150:
                CmdEjectWithError("Can't Be Played in Embedded Player");
                break;
            default:
                CmdEjectWithError($"Unknown Error ({code})");
                break;
            
        }
    }

    private bool ClientInGracePeriod(uint id)
    {
        return ClientJoinGracePeriod > NetworkTime.time - _clientsJoinedNetworkTime[id];
    }

    private static bool ClientPingedRecently(double pingTime)
    {
        return ClientPingTolerance > NetworkTime.time - pingTime;
    }

    private bool ClientVideoIsSynced(double timeStamp)
    {
        var whereTheyShouldBe = _idealScrub.CurrentTimeStamp(NetworkTime.time);
        var whereTheyAre = timeStamp;
        var synced = Math.Abs(whereTheyAre - whereTheyShouldBe) < VideoSyncTolerance;
        if (!synced)
        {
            TLog($"Client reported timestamp {whereTheyAre} which is not within {VideoSyncTolerance} seconds of {whereTheyShouldBe}");
        }

        return synced;
    }

    private void ReloadYouTube(string id, double timeStamp, Vector2 aspect)
    {
        TLog($"NavHome then load {id} at {timeStamp}");
        StopLoggingVideo();
        var resolution = _autoBrowser.ChangeAspect(aspect);
        _autoBrowser.PostMessages(new[]
        {
            YouTubeMessage.NavHome,
            YouTubeMessage.LoadYouTube(id, timeStamp, resolution.x, resolution.y)
        });
    }

    private bool AllClientsReportPlayerStatus(PlayerState playerState)
    {
        if (_clientsPlayerStatus.Count != NetworkServer.connections.Count)
        {
            return false;
        }

        return _clientsPlayerStatus.Values.All(status => status == playerState);
    }

    public void ButtonReloadBrowser()
    {
        SetupBrowser(true);
    }

    [Server]
    public void ButtonHardReload()
    {
        HardReload("pressed the hard reload button");
    }

    [Server]
    private void HardReload(string reason = "no reason provided")
    {
        TLog($"Initiating a hard reload because [{reason}]");
        _clientsLastPing.Clear();
        _clientsPlayerStatus.Clear();
        _beginReadyUpTime = NetworkTime.time;
        _idealScrub = _idealScrub.Pause(NetworkTime.time);

        var timeStamp = _idealScrub.CurrentTimeStamp(NetworkTime.time);
        togglePause.ServerSetPaused(false);
        RpcReloadYouTube(_serverContentInfo.ID, timeStamp, _serverContentInfo.Aspect);
    }

    private void HardSeekTo(float timeStamp)
    {
        _idealScrub = ScrubData.PausedAtScrub(timeStamp);
        BeginSync();
        togglePause.ServerSetPaused(false);
        RpcReloadYouTube(_serverContentInfo.ID, timeStamp, _serverContentInfo.Aspect);
    }

    [Server]
    private void LoadVideo(string id, double timeStamp)
    {
        togglePause.ServerSetPaused(false);

        TLog($"Fetching info for video {id}");

        if (_fetchAndReadyUp != null)
        {
            StopCoroutine(_fetchAndReadyUp);
        }

        _fetchAndReadyUp = StartCoroutine(FetchYouTubeAspect(id, aspect =>
        {
            TLog($"Fetched aspect ({aspect.x},{aspect.y}) for video ({id})");

            _serverContentInfo = new ContentInfo(true, id, aspect);
            _contentActive = true;
            _contentInfoAtTime = NetworkTime.time;
            _idealScrub = ScrubData.PausedAtScrub(timeStamp);

            BeginSync("new video");
            RpcReloadYouTube(id, timeStamp, aspect);

            _fetchAndReadyUp = null;
        }));
    }

    [Command(ignoreAuthority = true)]
    private void CmdPingAndCheckTimeStamp(uint id, double networkTime, double timeStamp)
    {
        _clientsLastPing[id] = NetworkTime.time;

        if (_serverVideoEnded)
        {
            return;
        }

        // TODO could use networkTime of timeStamp to account for rtt
        if (_allGood && !ClientInGracePeriod(id) && !ClientVideoIsSynced(timeStamp))
        {
            BeginSync("Client reported a bad timestamp in ping");
            RpcReadyUp(_idealScrub.CurrentTimeStamp(NetworkTime.time));
        }
    }

    [Command(ignoreAuthority = true)]
    private void CmdUpdateClientPlayerStatus(uint id, PlayerState playerState)
    {
        TLog($"Client [{id}] is {playerState}");
        _clientsPlayerStatus[id] = playerState;
    }

    [Command(ignoreAuthority = true)]
    private void CmdLoadVideo(string id, double timeStamp)
    {
        LoadVideo(id, timeStamp);
    }

    [Command(ignoreAuthority = true)]
    private void CmdCloseVideo()
    {
        CloseVideo();
    }

    [Command(ignoreAuthority = true)]
    public void CmdReadyUp(double timestamp)
    {
        if (_serverVideoEnded && _serverContentInfo.Active)
        {
            HardSeekTo((float) timestamp);
        }
        else
        {
            _idealScrub = ScrubData.PausedAtScrub(timestamp);
            BeginSync("CmdReadyUp");
            RpcReadyUp(_idealScrub.CurrentTimeStamp(NetworkTime.time));
        }
    }

    [Command(ignoreAuthority = true)]
    private void CmdHandleVideoEnded(float endingTimeStamp)
    {
        _serverVideoEnded = true;
        _idealScrub = ScrubData.PausedAtScrub(endingTimeStamp);
    }
    
    [Command(ignoreAuthority = true)]
    public void CmdSetVolumeLevel(float volumeLevel)
    {
        _volumeLevel = Mathf.Clamp(VolumeMax * volumeLevel, 0, VolumeMax);
    }

    [Server]
    private void CloseVideo()
    {
        // todo set paused
        // todo lower the screen
        _serverContentInfo = new ContentInfo(false, "", new Vector2(1, 1));
        _contentActive = false;
        togglePause.SetInteractable(false);
        RpcGoHome();
    }

    [TargetRpc]
    private void TargetReloadYoutube(NetworkConnection target, string id, double timeStamp, Vector2 aspect)
    {
        TLog("[Target RPC] ReloadYouTube");
        ReloadYouTube(id, timeStamp, aspect);
    }

    [Client]
    private void HandleSeekPlayer(object sender, float ts)
    {
        TLog($"Seek player to time stamp {ts}");
        CmdReadyUp(ts);
    }

    [ClientRpc]
    private void RpcReloadYouTube(string id, double timeStamp, Vector2 aspect)
    {
        TLog("[RPC] ReloadYouTube");
        ReloadYouTube(id, timeStamp, aspect);
    }

    [ClientRpc]
    private void RpcReadyUp(double timeStamp)
    {
        TLog($"[RPC] Ready up at timestamp {timeStamp}");
        _autoBrowser.PostMessage(YouTubeMessage.ReadyUpAtTime(timeStamp));
    }

    [TargetRpc]
    private void TargetReadyUp(NetworkConnection target, double timeStamp)
    {
        TLog($"[TARGET RPC] Ready up at {timeStamp}");
        _autoBrowser.PostMessage(YouTubeMessage.ReadyUpAtTime(timeStamp));
    }

    [ClientRpc]
    private void RpcGoHome()
    {
        TLog("Navigating home");
        StopLoggingVideo();
        _autoBrowser.PostMessage(YouTubeMessage.NavHome);
    }

    private IEnumerator FetchYouTubeAspect(string videoId, Action<Vector2> callback)
    {
        var newAspect = new Vector2(16, 9);

        // todo fix this
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        var videoInfoUrl = $"https://api.desk.link:8080/v1/youtube/{videoId}";
        #else
        var videoInfoUrl = $"https://api.desk.link:1776/v1/youtube/{videoId}";
        #endif

        using (var www = UnityWebRequest.Get(videoInfoUrl))
        {
            var req = www.SendWebRequest();

            yield return req;

            if (!(www.isHttpError || www.isNetworkError))
            {
                var jsonNode = JSONNode.Parse(www.downloadHandler.text) as JSONObject;
                if (jsonNode?["width"] != null && jsonNode["height"] != null)
                {
                    var width = (float) jsonNode["width"];
                    var height = (float) jsonNode["height"];
                    var liveNow = (bool) jsonNode["liveNow"];

                    Debug.Log($"xx {liveNow}");
                    if (liveNow)
                    {
                        RpcAddMessageToStack("Livestreams not supported yet");
                        videoCubeSpot.ServerEjectCurrentVideo();
                    }
                    
                    newAspect = new Vector2(width, height);
                }
            }
        }

        callback(newAspect);
    }

    private string Tag()
    {
        switch (isClient)
        {
            case true when isServer:
                return NetworkClient.connection.isReady
                    ? $"<color=orange>BonsaiABC (host_{NetworkClient.connection.identity.netId}):</color> "
                    : "<color=orange>BonsaiABC (host):</color> ";
            case true:
                return NetworkClient.connection.isReady
                    ? $"<color=orange>BonsaiABC (client_{NetworkClient.connection.identity.netId}):</color> "
                    : "<color=orange>BonsaiABC (client):</color> ";
            default:
                return "[BONSAI SERVER] ";
        }
    }

    private void TLog(string message)
    {
        Debug.Log(Tag() + message);
    }

    private void BonsaiLog(string msg)
    {
        Debug.Log("<color=orange>BonsaiABC: </color>: " + msg);
    }

    private void BonsaiLogWarning(string msg)
    {
        Debug.LogWarning("<color=orange>BonsaiABC: </color>: " + msg);
    }

    private void BonsaiLogError(string msg)
    {
        Debug.LogError("<color=orange>BonsaiABC: </color>: " + msg);
    }

    public MediaInfo GetMediaInfo()
    {
        if (_contentActive)
        {
            return new MediaInfo
            {
                Active = true,
                Name = "Unknown",
                Paused = !_idealScrub.Active,
                Scrub = (float) _idealScrub.CurrentTimeStamp(NetworkTime.time),
                Duration = _clientPlayerDuration,
                VolumeLevel = _volumeLevel,
                VolumeMax = VolumeMax
            };
        }

        return new MediaInfo();
    }

    private enum PlayerState
    {
        Unstarted,
        Ready,
        Paused,
        Playing,
        Buffering,
        Ended
    }

    private static class YouTubeMessage
    {
        public const string Play = "{\"type\": \"video\", \"command\": \"play\"}";
        public const string Pause = "{\"type\": \"video\", \"command\": \"pause\"}";

        public const string MaskOn = "{" + "\"type\": \"video\", " + "\"command\": \"maskOn\" " + "}";
        public const string MaskOff = "{" + "\"type\": \"video\", " + "\"command\": \"maskOff\" " + "}";
        public static readonly string NavHome = PushPath("/home");

        private static string PushPath(string path)
        {
            return "{" + "\"type\": \"nav\", " + "\"command\": \"push\", " + $"\"path\": \"{path}\"" + "}";
        }

        public static string LoadYouTube(string id, double ts, int x = 0, int y = 0)
        {
            var resQuery = "";
            if (x != 0 && y != 0)
            {
                resQuery = $"?x={x}&y={y}";
            }

            return "{" + "\"type\": \"nav\", " + "\"command\": \"push\", " + $"\"path\": \"/youtube/{id}/{ts}{resQuery}\"" + "}";
        }

        public static string ReadyUpAtTime(double timeStamp)
        {
            return "{" + "\"type\": \"video\", " + "\"command\": \"readyUp\", " + $"\"timeStamp\": {timeStamp}" + "}";
        }

        public static string SetVolume(double level)
        {
            return "{" + "\"type\": \"video\", " + "\"command\": \"setVolume\", " + $"\"level\": {level}" + "}";
        }
    }

    private readonly struct ContentInfo
    {
        public readonly bool Active;
        public readonly string ID;
        public readonly Vector2 Aspect;

        public ContentInfo(bool active, string id, Vector2 aspect)
        {
            Active = active;
            ID = id;
            Aspect = aspect;
        }
    }

    public readonly struct ScrubData
    {
        public readonly double Scrub;
        public readonly double NetworkTimeActivated;
        public readonly bool Active;

        public override string ToString()
        {
            return $"{Scrub} {NetworkTimeActivated} {Active}";
        }

        private ScrubData(double scrub, double networkTimeActivated, bool active)
        {
            Scrub = scrub;
            NetworkTimeActivated = networkTimeActivated;
            Active = active;
        }

        public static ScrubData PausedAtScrub(double scrub)
        {
            return new ScrubData(scrub, -1, false);
        }

        public ScrubData Pause(double networkTime)
        {
            return new ScrubData(CurrentTimeStamp(networkTime), -1, false);
        }

        public ScrubData UnPauseAtNetworkTime(double networkTime)
        {
            if (Active)
            {
                Debug.LogError("Scrub should be paused before resuming");
            }

            return new ScrubData(Scrub, networkTime, true);
        }

        public double CurrentTimeStamp(double networkTime)
        {
            if (!Active || networkTime - NetworkTimeActivated < 0)
            {
                return Scrub;
            }

            return Scrub + (networkTime - NetworkTimeActivated);
        }

        public bool IsStarted(double networkTime)
        {
            return Active && networkTime > NetworkTimeActivated;
        }
    }

    public class MediaInfo
    {
        public bool Active;
        public float Duration;
        public string Name;
        public bool Paused;
        public float Scrub;
        public float VolumeLevel;
        public float VolumeMax;

        public MediaInfo()
        {
            Active = false;
            Name = "None";
            Paused = true;
            Scrub = 0f;
            Duration = 1f;
            VolumeLevel = 0f;
        }
    }

    void OnApplicationQuit()
    {
        StopLoggingVideo();
    }

    void OnDestroy()
    {
        StopLoggingVideo();
    }

    private void OnDisable()
    {
        StopLoggingVideo();
    }
}