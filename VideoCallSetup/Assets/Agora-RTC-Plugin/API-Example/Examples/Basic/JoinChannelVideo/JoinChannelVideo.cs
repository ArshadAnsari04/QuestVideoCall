using Agora.Rtc;
using io.agora.rtc.demo;
using PassthroughCameraSamples;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace Agora_RTC_Plugin.API_Example.Examples.Basic.JoinChannelVideo
{
    public class JoinChannelVideo : MonoBehaviour
    {
        [FormerlySerializedAs("appIdInput")]
        [SerializeField]
        private AppIdInput _appIdInput;

        [Header("_____________Basic Configuration_____________")]
        [FormerlySerializedAs("APP_ID")]
        [SerializeField]
        private string _appID = "";

        [FormerlySerializedAs("TOKEN")]
        [SerializeField]
        private string _token = "";

        [FormerlySerializedAs("CHANNEL_NAME")]
        [SerializeField]
        private string _channelName = "";

        public Text LogText;
        internal Logger Log;
        internal IRtcEngine RtcEngine = null;

        public Dropdown _videoDeviceSelect;
        private IVideoDeviceManager _videoDeviceManager;
        private DeviceInfo[] _videoDeviceInfos;
        public Dropdown _areaSelect;
        public GameObject _videoQualityItemPrefab;

        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        [SerializeField] private Text m_debugText;
        [SerializeField] private RawImage m_image;
        private WebCamTexture _videoSource; // Unified video source for both platforms
        private Texture2D _passthroughTexture;
        private Coroutine _pushFramesCoroutine;
        internal bool _joinedChannel = false;

        private void Start()
        {
            LoadAssetData();
            PrepareAreaList();
            if (CheckAppId())
            {
                RtcEngine = Agora.Rtc.RtcEngine.CreateAgoraRtcEngine();
                InitEngine();
                StartCoroutine(InitializeVideoSource());
                Invoke("JoinChannel", 3); // Delay to ensure video source is ready
            }

#if UNITY_IOS || UNITY_ANDROID
            var text = GameObject.Find("VideoCanvas/Scroll View/Viewport/Content/VideoDeviceManager")?.GetComponent<Text>();
            if (text != null) text.text = "Video device manager not supported on this platform";
            GameObject.Find("VideoCanvas/Scroll View/Viewport/Content/VideoDeviceButton")?.SetActive(false);
            GameObject.Find("VideoCanvas/Scroll View/Viewport/Content/deviceIdSelect")?.SetActive(false);
            GameObject.Find("VideoCanvas/Scroll View/Viewport/Content/VideoSelectButton")?.SetActive(false);
#endif
        }

        private IEnumerator InitializeVideoSource()
        {
#if UNITY_ANDROID && !UNITY_EDITOR // Quest 3 passthrough
            if (m_webCamTextureManager == null)
            {
                Log.UpdateLog("Error: WebCamTextureManager is not assigned.");
                yield break;
            }

            while (m_webCamTextureManager.WebCamTexture == null)
            {
                Log.UpdateLog("Waiting for Quest 3 passthrough WebCamTexture...");
                yield return null;
            }

            _videoSource = m_webCamTextureManager.WebCamTexture;
            if (!_videoSource.isPlaying)
            {
                _videoSource.Play();
                while (!_videoSource.isPlaying)
                {
                    Log.UpdateLog("Starting Quest 3 passthrough camera...");
                    yield return null;
                }
            }

            Log.UpdateLog("Quest 3 passthrough camera initialized.");
#else // Windows
            if (WebCamTexture.devices.Length == 0)
            {
                Log.UpdateLog("Error: No webcam devices found on Windows.");
                yield break;
            }

            _videoSource = new WebCamTexture();
            _videoSource.Play();
            while (!_videoSource.isPlaying)
            {
                Log.UpdateLog("Waiting for Windows webcam to start...");
                yield return null;
            }

            Log.UpdateLog("Windows webcam initialized.");
#endif

            int width = _videoSource.width;
            int height = _videoSource.height;
            if (width <= 0 || height <= 0)
            {
                Log.UpdateLog($"Error: Invalid video dimensions ({width}x{height}).");
                yield break;
            }

            try
            {
                _passthroughTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Log.UpdateLog($"Texture buffer created: {width}x{height}");
                m_image.texture = _videoSource;
                m_debugText.text += "\nVideo source ready and playing.";
            }
            catch (Exception e)
            {
                Log.UpdateLog($"Failed to create passthroughTexture: {e.Message}");
                yield break;
            }

            _pushFramesCoroutine = StartCoroutine(PushPassthroughFramesToAgora());
        }

        private IEnumerator PushPassthroughFramesToAgora()
        {
            if (RtcEngine == null || _passthroughTexture == null)
            {
                Log.UpdateLog("Error: RtcEngine or passthroughTexture is null.");
                yield break;
            }

            while (!_joinedChannel)
            {
                Log.UpdateLog("Waiting for channel join...");
                yield return null;
            }

            // Configure Agora to use custom video track
            var options = new ChannelMediaOptions();
            options.publishCameraTrack.SetValue(false); // Disable default camera
            options.publishCustomVideoTrack.SetValue(true); // Enable custom feed
            int ret = RtcEngine.UpdateChannelMediaOptions(options);
            Log.UpdateLog($"UpdateChannelMediaOptions for custom video: {ret}");

            while (true)
            {
                if (_videoSource == null || !_videoSource.isPlaying)
                {
                    Log.UpdateLog("Error: Video source is null or not playing.");
                    yield break;
                }

                try
                {
                    _passthroughTexture.SetPixels(_videoSource.GetPixels());
                    _passthroughTexture.Apply();

                    byte[] frameData = _passthroughTexture.GetRawTextureData().ToArray();
                    if (frameData.Length == 0)
                    {
                        Log.UpdateLog("Error: Empty frame data.");
                        continue;
                    }

                    ExternalVideoFrame externalFrame = new ExternalVideoFrame
                    {
                        type = VIDEO_BUFFER_TYPE.VIDEO_BUFFER_RAW_DATA,
                        format = VIDEO_PIXEL_FORMAT.VIDEO_PIXEL_RGBA,
                        buffer = frameData,
                        stride = _passthroughTexture.width, // Stride in pixels, not bytes
                        height = _passthroughTexture.height,
                        timestamp = (long)(Time.time * 1000)
                    };

                    Log.UpdateLog($"Pushing frame: width={_passthroughTexture.width}, height={_passthroughTexture.height}, stride={externalFrame.stride}, buffer size={frameData.Length}");
                    ret = RtcEngine.PushVideoFrame(externalFrame);
                    if (ret != 0)
                    {
                        Log.UpdateLog($"PushVideoFrame failed: {ret}");
                    }
                    else
                    {
                        Log.UpdateLog("PushVideoFrame succeeded");
                    }
                }
                catch (Exception e)
                {
                    Log.UpdateLog($"PushPassthroughFramesToAgora error: {e.Message}\nStack: {e.StackTrace}");
                    yield break;
                }

                yield return new WaitForSeconds(0.033f); // ~30 FPS
            }
        }

        private void Update()
        {
            PermissionHelper.RequestMicrophontPermission();
            PermissionHelper.RequestCameraPermission();
#if UNITY_ANDROID && !UNITY_EDITOR
           m_debugText.text = PassthroughCameraPermissions.HasCameraPermission == true ? "Permission granted." : "No permission granted.";
#else
            m_debugText.text = "Running on Windows.";
#endif
        }

        private void LoadAssetData()
        {
            if (_appIdInput == null) return;
            _appID = _appIdInput.appID;
            _token = _appIdInput.token;
            _channelName = _appIdInput.channelName;
        }

        private bool CheckAppId()
        {
            Log = new Logger(LogText);
            return Log.DebugAssert(_appID.Length > 10, "Please fill in your appId in API-Example/profile/appIdInput.asset");
        }

        private void PrepareAreaList()
        {
            int index = 0;
            var areaList = new List<Dropdown.OptionData>();
            var enumNames = Enum.GetNames(typeof(AREA_CODE));
            foreach (var name in enumNames)
            {
                areaList.Add(new Dropdown.OptionData(name));
                if (name == "AREA_CODE_GLOB") index = areaList.Count - 1;
            }
            _areaSelect.ClearOptions();
            _areaSelect.AddOptions(areaList);
            _areaSelect.value = index;
        }

        public void InitEngine()
        {
            var areaCode = (AREA_CODE)Enum.Parse(typeof(AREA_CODE), _areaSelect.captionText.text);
            Log.UpdateLog($"Select AREA_CODE: {areaCode}");

            var handler = new UserEventHandler(this);
            var context = new RtcEngineContext
            {
                appId = _appID,
                channelProfile = CHANNEL_PROFILE_TYPE.CHANNEL_PROFILE_LIVE_BROADCASTING,
                audioScenario = AUDIO_SCENARIO_TYPE.AUDIO_SCENARIO_DEFAULT,
                areaCode = areaCode
            };

            int result = RtcEngine.Initialize(context);
            Log.UpdateLog($"Initialize result: {result}");
            if (result != 0) return;

            RtcEngine.InitEventHandler(handler);
            RtcEngine.EnableAudio();
            RtcEngine.EnableVideo();
            RtcEngine.SetChannelProfile(CHANNEL_PROFILE_TYPE.CHANNEL_PROFILE_LIVE_BROADCASTING);
            RtcEngine.SetClientRole(CLIENT_ROLE_TYPE.CLIENT_ROLE_BROADCASTER);

            var config = new VideoEncoderConfiguration
            {
                dimensions = new VideoDimensions(640, 360),
                frameRate = 15,
                bitrate = 400 // Non-zero bitrate for stable encoding
            };
            RtcEngine.SetVideoEncoderConfiguration(config);
        }

        public void JoinChannel()
        {
            int ret = RtcEngine.JoinChannel(_token, _channelName, "", 0);
            Log.UpdateLog($"JoinChannel result: {ret}");
           // var node = MakeVideoView(0);
           // CreateLocalVideoCallQualityPanel(node);
        }

        public void LeaveChannel()
        {
            RtcEngine.LeaveChannel();
            _joinedChannel = false;
        }

        public void StartPreview()
        {
            RtcEngine.StartPreview();
            var node = MakeVideoView(0);
            CreateLocalVideoCallQualityPanel(node);
        }

        public void StopPreview()
        {
            DestroyVideoView(0);
            RtcEngine.StopPreview();
        }

        public void StartPublish()
        {
            var options = new ChannelMediaOptions();
            options.publishMicrophoneTrack.SetValue(true);
            options.publishCustomVideoTrack.SetValue(true); // Use custom video track
            int ret = RtcEngine.UpdateChannelMediaOptions(options);
            Log.UpdateLog($"StartPublish: {ret}");
        }

        public void StopPublish()
        {
            var options = new ChannelMediaOptions();
            options.publishMicrophoneTrack.SetValue(false);
            options.publishCustomVideoTrack.SetValue(false); // Stop custom video track
            int ret = RtcEngine.UpdateChannelMediaOptions(options);
            Log.UpdateLog($"StopPublish: {ret}");
        }

        public void AdjustVideoEncodedConfiguration640()
        {
            var config = new VideoEncoderConfiguration
            {
                dimensions = new VideoDimensions(640, 360),
                frameRate = 15,
                bitrate = 400
            };
            RtcEngine.SetVideoEncoderConfiguration(config);
        }

        public void AdjustVideoEncodedConfiguration480()
        {
            var config = new VideoEncoderConfiguration
            {
                dimensions = new VideoDimensions(480, 480),
                frameRate = 15,
                bitrate = 400
            };
            RtcEngine.SetVideoEncoderConfiguration(config);
        }

        public void GetVideoDeviceManager()
        {
#if !UNITY_IOS && !UNITY_ANDROID
            _videoDeviceSelect.ClearOptions();
            _videoDeviceManager = RtcEngine.GetVideoDeviceManager();
            _videoDeviceInfos = _videoDeviceManager.EnumerateVideoDevices();
            Log.UpdateLog($"VideoDeviceManager count: {_videoDeviceInfos.Length}");
            for (int i = 0; i < _videoDeviceInfos.Length; i++)
            {
                Log.UpdateLog($"Device {i}: {_videoDeviceInfos[i].deviceName}, ID: {_videoDeviceInfos[i].deviceId}");
            }
            _videoDeviceSelect.AddOptions(_videoDeviceInfos.Select(w => new Dropdown.OptionData($"{w.deviceName} :{w.deviceId}")).ToList());
#endif
        }

        public void SelectVideoCaptureDevice()
        {
#if !UNITY_IOS && !UNITY_ANDROID
            if (_videoDeviceSelect == null || _videoDeviceSelect.options.Count == 0) return;
            var option = _videoDeviceSelect.options[_videoDeviceSelect.value].text;
            var deviceId = option.Split(':')[1];
            int ret = _videoDeviceManager.SetDevice(deviceId);
            Log.UpdateLog($"SelectVideoCaptureDevice: {ret}, DeviceId: {deviceId}");

            if (_videoSource != null) _videoSource.Stop();
            _videoSource = new WebCamTexture(deviceId);
            _videoSource.Play();
            _passthroughTexture = new Texture2D(_videoSource.width, _videoSource.height, TextureFormat.RGBA32, false);
            m_image.texture = _videoSource;
#endif
        }

        private void OnDestroy()
        {
            Log.UpdateLog("OnDestroy called");

            if (_pushFramesCoroutine != null)
            {
                StopCoroutine(_pushFramesCoroutine);
                _pushFramesCoroutine = null;
                Log.UpdateLog("PushPassthroughFramesToAgora coroutine stopped");
            }

            if (_videoSource != null && _videoSource.isPlaying)
            {
                _videoSource.Stop();
                Log.UpdateLog("Video source stopped");
            }

            if (_passthroughTexture != null)
            {
                Destroy(_passthroughTexture);
                _passthroughTexture = null;
                Log.UpdateLog("Passthrough texture destroyed");
            }

            if (RtcEngine != null)
            {
                RtcEngine.InitEventHandler(null);
                RtcEngine.LeaveChannel();
                RtcEngine.Dispose();
                RtcEngine = null;
                Log.UpdateLog("RtcEngine disposed");
            }
        }

        internal string GetChannelName() => _channelName;

        internal static GameObject MakeVideoView(uint uid, string channelId = "")
        {
            var go = GameObject.Find(uid.ToString());
            if (go != null) return go;

            var videoSurface = MakeImageSurface(uid.ToString());
            if (videoSurface == null) return null;

            videoSurface.SetForUser(uid, channelId, uid == 0 ? VIDEO_SOURCE_TYPE.VIDEO_SOURCE_CAMERA : VIDEO_SOURCE_TYPE.VIDEO_SOURCE_REMOTE);
            videoSurface.OnTextureSizeModify += (width, height) =>
            {
                var transform = videoSurface.GetComponent<RectTransform>();
                //  if (transform) transform.sizeDelta = new Vector2(width / 2, height / 2);
            #if UNITY_ANDROID && !UNITY_EDITOR
                            if (transform) transform.sizeDelta = new Vector2(640, 360);
            #else
                            if (transform) transform.sizeDelta = new Vector2(1450, 1080);
            #endif

                Debug.Log($"OnTextureSizeModify: {width}x{height}");
            };
            videoSurface.SetEnable(true);
            return videoSurface.gameObject;
        }

        private static VideoSurface MakeImageSurface(string goName)
        {
            var go = new GameObject(goName);
            go.AddComponent<RawImage>();
            go.AddComponent<UIElementDrag>();
            var canvas = GameObject.Find("VideoCanvas");
            if (canvas != null) go.transform.parent = canvas.transform;
            go.transform.Rotate(0f, 0f, 0f);
           
            go.transform.localPosition = new Vector3(200,0,0);
            go.transform.localScale = new Vector3(1, 1, 1f);
           // go.GetComponent<RectTransform>().sizeDelta = new Vector2(640, 360);
            return go.AddComponent<VideoSurface>();
        }

        internal static void DestroyVideoView(uint uid)
        {
            var go = GameObject.Find(uid.ToString());
            if (go != null) Destroy(go);
        }

        public void CreateLocalVideoCallQualityPanel(GameObject parent)
        {
            if (parent.GetComponentInChildren<LocalVideoCallQualityPanel>() != null) return;
            var panel = Instantiate(_videoQualityItemPrefab, parent.transform);
            panel.AddComponent<LocalVideoCallQualityPanel>();
        }

        public LocalVideoCallQualityPanel GetLocalVideoCallQualityPanel()
        {
            var go = GameObject.Find("0");
            return go?.GetComponentInChildren<LocalVideoCallQualityPanel>();
        }

        public void CreateRemoteVideoCallQualityPanel(GameObject parent, uint uid)
        {
            if (parent.GetComponentInChildren<RemoteVideoCallQualityPanel>() != null) return;
            var panel = Instantiate(_videoQualityItemPrefab, parent.transform);
            panel.transform.localPosition = new Vector3(0, -182, 0);
            panel.transform.Rotate(0f, 0f, 0f);
           // panel.GetComponent<RectTransform>().sizeDelta = new Vector2(640, 360);
            var comp = panel.AddComponent<RemoteVideoCallQualityPanel>();
            comp.Uid = uid;
        }

        public RemoteVideoCallQualityPanel GetRemoteVideoCallQualityPanel(uint uid)
        {
            var go = GameObject.Find(uid.ToString());
            return go?.GetComponentInChildren<RemoteVideoCallQualityPanel>();
        }
    }

    internal class UserEventHandler : IRtcEngineEventHandler
    {
        private readonly JoinChannelVideo _videoSample;

        internal UserEventHandler(JoinChannelVideo videoSample) => _videoSample = videoSample;

        public override void OnJoinChannelSuccess(RtcConnection connection, int elapsed)
        {
            int build = 0;
            _videoSample.Log.UpdateLog($"SDK Version: {_videoSample.RtcEngine.GetVersion(ref build)}, Build: {build}");
            _videoSample.Log.UpdateLog($"OnJoinChannelSuccess: {connection.channelId}, UID: {connection.localUid}, Elapsed: {elapsed}");
            _videoSample._joinedChannel = true;
        }

        public override void OnError(int err, string msg) => _videoSample.Log.UpdateLog($"OnError: {err}, Msg: {msg}");

        public override void OnRejoinChannelSuccess(RtcConnection connection, int elapsed) => _videoSample.Log.UpdateLog("OnRejoinChannelSuccess");

        public override void OnLeaveChannel(RtcConnection connection, RtcStats stats) => _videoSample.Log.UpdateLog("OnLeaveChannel");

        public override void OnClientRoleChanged(RtcConnection connection, CLIENT_ROLE_TYPE oldRole, CLIENT_ROLE_TYPE newRole, ClientRoleOptions newRoleOptions) =>
            _videoSample.Log.UpdateLog("OnClientRoleChanged");

        public override void OnUserJoined(RtcConnection connection, uint uid, int elapsed)
        {
            _videoSample.Log.UpdateLog($"OnUserJoined: UID: {uid}, Elapsed: {elapsed}");
            var node = JoinChannelVideo.MakeVideoView(uid, _videoSample.GetChannelName());
            _videoSample.CreateRemoteVideoCallQualityPanel(node, uid);
        }

        public override void OnUserOffline(RtcConnection connection, uint uid, USER_OFFLINE_REASON_TYPE reason) =>
            _videoSample.Log.UpdateLog($"OnUserOffline: UID: {uid}, Reason: {(int)reason}");

        public override void OnRtcStats(RtcConnection connection, RtcStats stats)
        {
            var panel = _videoSample.GetLocalVideoCallQualityPanel();
            if (panel != null) { panel.Stats = stats; panel.RefreshPanel(); }
        }

        public override void OnLocalAudioStats(RtcConnection connection, LocalAudioStats stats)
        {
            var panel = _videoSample.GetLocalVideoCallQualityPanel();
            if (panel != null) { panel.AudioStats = stats; panel.RefreshPanel(); }
        }

        public override void OnLocalVideoStats(RtcConnection connection, LocalVideoStats stats)
        {
            var panel = _videoSample.GetLocalVideoCallQualityPanel();
            if (panel != null) { panel.VideoStats = stats; panel.RefreshPanel(); }
        }

        public override void OnRemoteVideoStats(RtcConnection connection, RemoteVideoStats stats)
        {
            var panel = _videoSample.GetRemoteVideoCallQualityPanel(stats.uid);
            if (panel != null) { panel.VideoStats = stats; panel.RefreshPanel(); }
        }
    }
}