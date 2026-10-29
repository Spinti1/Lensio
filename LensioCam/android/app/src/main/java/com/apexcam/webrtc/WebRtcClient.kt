package com.apexcam.webrtc

import android.content.Context
import android.util.Log
import org.webrtc.*

class WebRtcClient(
    private val context: Context,
    private val rootEglBase: EglBase,
    private val signalingClient: SignalingClient
) {
    private val tag = "WebRtcClient"
    private var peerConnectionFactory: PeerConnectionFactory? = null
    private var peerConnection: PeerConnection? = null
    private var videoSource: VideoSource? = null
    private var videoTrack: VideoTrack? = null
    private var audioSource: AudioSource? = null
    private var audioTrack: AudioTrack? = null
    private var surfaceTextureHelper: SurfaceTextureHelper? = null



    private fun initializePeerConnectionFactory() {
        val options = PeerConnectionFactory.InitializationOptions.builder(context)
            .setEnableInternalTracer(false)
            .createInitializationOptions()
        PeerConnectionFactory.initialize(options)

        val encoderFactory = DefaultVideoEncoderFactory(
            rootEglBase.eglBaseContext,
            true, // enableIntelVp8
            true  // enableH264HighProfile
        )
        val decoderFactory = DefaultVideoDecoderFactory(rootEglBase.eglBaseContext)

        peerConnectionFactory = PeerConnectionFactory.builder()
            .setVideoEncoderFactory(encoderFactory)
            .setVideoDecoderFactory(decoderFactory)
            .setOptions(PeerConnectionFactory.Options().apply {
                disableEncryption = false
                networkIgnoreMask = 0
            })
            .createPeerConnectionFactory()

        Log.d(tag, "PeerConnectionFactory inițializat cu Encoder Hardware H.264")
    }

    fun startMediaTracks() {
        val factory = peerConnectionFactory ?: return

        // 1. Audio Track (Microphone)
        val audioConstraints = MediaConstraints().apply {
            mandatory.add(MediaConstraints.KeyValuePair("googEchoCancellation", "false"))
            mandatory.add(MediaConstraints.KeyValuePair("googAutoGainControl", "true"))
            mandatory.add(MediaConstraints.KeyValuePair("googNoiseSuppression", "true"))
            mandatory.add(MediaConstraints.KeyValuePair("googHighpassFilter", "true"))
        }
        audioSource = factory.createAudioSource(audioConstraints)
        audioTrack = factory.createAudioTrack("APEXCAM_AUDIO_TRACK", audioSource)
        audioTrack?.setEnabled(true)

        // 2. Video Track
        surfaceTextureHelper = SurfaceTextureHelper.create("ApexCamSurfaceThread", rootEglBase.eglBaseContext)
        videoSource = factory.createVideoSource(false)
        videoTrack = factory.createVideoTrack("APEXCAM_VIDEO_TRACK", videoSource)
        videoTrack?.setEnabled(true)

        Log.d(tag, "Piste Audio & Video inițializate")
    }

    fun createPeerConnection() {
        if (peerConnectionFactory == null) initializePeerConnectionFactory()
        val factory = peerConnectionFactory ?: return
        val rtcConfig = PeerConnection.RTCConfiguration(emptyList()).apply {
            tcpCandidatePolicy = PeerConnection.TcpCandidatePolicy.ENABLED
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            rtcpMuxPolicy = PeerConnection.RtcpMuxPolicy.REQUIRE
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
            keyType = PeerConnection.KeyType.ECDSA
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
        }

        peerConnection = factory.createPeerConnection(rtcConfig, object : PeerConnection.Observer {
            override fun onIceCandidate(candidate: IceCandidate) {
                Log.d(tag, "Candidat ICE generat: ${candidate.sdpMid}")
                signalingClient.sendIceCandidate(candidate)
            }

            override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>?) {}

            override fun onSignalingChange(state: PeerConnection.SignalingState?) {
                Log.d(tag, "Stare semnalizare: $state")
            }

            override fun onIceConnectionChange(state: PeerConnection.IceConnectionState?) {
                Log.d(tag, "Stare conexiune ICE: $state")
            }

            override fun onIceConnectionReceivingChange(receiving: Boolean) {}

            override fun onIceGatheringChange(state: PeerConnection.IceGatheringState?) {
                Log.d(tag, "Stare colectare ICE: $state")
            }

            override fun onConnectionChange(state: PeerConnection.PeerConnectionState?) {
                Log.d(tag, "Stare PeerConnection: $state")
            }

            override fun onTrack(transceiver: RtpTransceiver?) {}
            override fun onDataChannel(channel: DataChannel?) {}
            override fun onRenegotiationNeeded() {}
            override fun onAddStream(stream: MediaStream?) {}
            override fun onRemoveStream(stream: MediaStream?) {}
        })

        // Add tracks to PeerConnection
        if (videoTrack != null) {
            peerConnection?.addTrack(videoTrack, listOf("APEXCAM_MEDIA_STREAM"))
        }
        if (audioTrack != null) {
            peerConnection?.addTrack(audioTrack, listOf("APEXCAM_MEDIA_STREAM"))
        }
    }

    fun sendOffer() {
        val sdpConstraints = MediaConstraints().apply {
            mandatory.add(MediaConstraints.KeyValuePair("OfferToReceiveAudio", "false"))
            mandatory.add(MediaConstraints.KeyValuePair("OfferToReceiveVideo", "false"))
        }

        peerConnection?.createOffer(object : SdpObserver {
            override fun onCreateSuccess(desc: SessionDescription) {
                peerConnection?.setLocalDescription(object : SdpObserver {
                    override fun onCreateSuccess(d: SessionDescription?) {}
                    override fun onSetSuccess() {
                        Log.d(tag, "Local SDP Offer stabilit cu succes, transmitere către PC...")
                        signalingClient.sendOffer(desc.description)
                    }
                    override fun onCreateFailure(s: String?) {}
                    override fun onSetFailure(s: String?) {}
                }, desc)
            }

            override fun onSetSuccess() {}
            override fun onCreateFailure(error: String?) {
                Log.e(tag, "Error creare SDP Offer: $error")
            }
            override fun onSetFailure(error: String?) {}
        }, sdpConstraints)
    }

    fun handleRemoteAnswer(sdpString: String) {
        val desc = SessionDescription(SessionDescription.Type.ANSWER, sdpString)
        peerConnection?.setRemoteDescription(object : SdpObserver {
            override fun onCreateSuccess(d: SessionDescription?) {}
            override fun onSetSuccess() {
                Log.d(tag, "Remote SDP Answer setat cu succes pe telefon! Conexiune activă!")
            }
            override fun onCreateFailure(s: String?) {}
            override fun onSetFailure(s: String?) {
                Log.e(tag, "Error setare Remote SDP: $s")
            }
        }, desc)
    }

    fun addRemoteIceCandidate(candidateObj: com.google.gson.JsonObject) {
        try {
            val sdpMid = candidateObj.get("sdpMid")?.asString ?: "0"
            val sdpMLineIndex = candidateObj.get("sdpMLineIndex")?.asInt ?: 0
            val sdp = candidateObj.get("candidate")?.asString ?: ""
            val candidate = IceCandidate(sdpMid, sdpMLineIndex, sdp)
            peerConnection?.addIceCandidate(candidate)
        } catch (e: Exception) {
            Log.e(tag, "Error adăugare candidat ICE lactual", e)
        }
    }

    fun setMuteAudio(mute: Boolean) {
        audioTrack?.setEnabled(!mute)
    }

    fun getVideoSource(): VideoSource? = videoSource

    fun close() {
        peerConnection?.dispose()
        peerConnection = null
        videoTrack?.dispose()
        audioTrack?.dispose()
        videoSource?.dispose()
        audioSource?.dispose()
        surfaceTextureHelper?.dispose()
        peerConnectionFactory?.dispose()
        peerConnectionFactory = null
    }
}
