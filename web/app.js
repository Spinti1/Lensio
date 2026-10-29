import {AutoConnect} from './auto-connect.js?v=6';
import {gatherIce} from './protocol.js';

const $=id=>document.getElementById(id),video=$('localVideo');
const start=$('start'),stop=$('stop'),placeholder=$('placeholder'),notice=$('message');
function saved(key,fallback=''){try{return localStorage.getItem('lensio.'+key)||fallback;}catch{return fallback;}}
function remember(key,value){try{localStorage.setItem('lensio.'+key,value);}catch{}}
const scanned=new URLSearchParams(location.hash.slice(1)).get('room');
const validRoom=value=>/^[a-f0-9]{64}$/.test(value||'');
const room=validRoom(scanned)?scanned:saved('pairRoom');
if(scanned)history.replaceState(null,'',location.pathname+location.search);
let id=saved('id');
if(!id){id=crypto.randomUUID?crypto.randomUUID():Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');remember('id',id);}
const phoneId='web:'+id;
const name=saved('name',/iPhone|iPad/.test(navigator.userAgent)?'iPhone':/Android/.test(navigator.userAgent)?'Android phone':'Web camera').slice(0,60);
let resolution=saved('resolution','720'),cameraId='',stream=null,peer=null,channel=null,discovery=null,wake=null;
let session=0,changing=false,controls=Promise.resolve();

function state(phase,text){document.body.dataset.phase=phase;$('state').textContent=text;}
function clearNotice(){notice.hidden=true;notice.textContent='';}
function showError(error){notice.textContent=error?.message||String(error);notice.hidden=false;}
async function keepAwake(){try{if(!wake&&navigator.wakeLock&&document.visibilityState==='visible'){const run=session,lock=await navigator.wakeLock.request('screen');if(run!==session||!stream){await lock.release();return;}wake=lock;wake.addEventListener('release',()=>{if(wake===lock)wake=null;});}}catch{}}
async function releaseWake(){if(wake){await wake.release().catch(()=>{});wake=null;}}
function closePeer(){const previous=peer;peer=null;channel=null;if(previous)previous.close();}

async function stopCamera(){
  session++;discovery?.close();discovery=null;closePeer();
  if(stream)stream.getTracks().forEach(t=>t.stop());stream=null;video.srcObject=null;
  await releaseWake();placeholder.hidden=false;start.disabled=changing;stop.disabled=true;clearNotice();state('off','Camera off');
}

async function capture(id,run){
  if(changing)return;changing=true;
  try{
    if(!navigator.mediaDevices?.getUserMedia)throw new Error('Open LensioCam in Safari or Chrome over HTTPS.');
    if(stream){stream.getTracks().forEach(t=>t.stop());stream=null;}
    const height=resolution==='1080'?1080:720;
    const constraints={width:{ideal:height*16/9},height:{ideal:height},frameRate:{ideal:30,max:30}};
    if(id)constraints.deviceId={exact:id};else constraints.facingMode={ideal:'environment'};
    let next;
    try{next=await navigator.mediaDevices.getUserMedia({video:constraints,audio:false});}
    catch(e){if(id&&['OverconstrainedError','NotFoundError'].includes(e.name)){delete constraints.deviceId;constraints.facingMode={ideal:'environment'};next=await navigator.mediaDevices.getUserMedia({video:constraints,audio:false});}else throw e;}
    if(run!==session){next.getTracks().forEach(t=>t.stop());return;}
    stream=next;const track=stream.getVideoTracks()[0];cameraId=track.getSettings().deviceId||'';
    remember('camera',cameraId);remember('resolution',resolution);
    track.addEventListener('ended',()=>{if(run===session&&stream?.getVideoTracks()[0]===track){stopCamera().then(()=>{state('error','Camera interrupted');showError(new Error('Camera access ended. Press Start camera & connect to retry.'));});}});
    video.srcObject=stream;await video.play();if(run!==session)return;
    const sender=peer?.getSenders().find(s=>s.track?.kind==='video');if(sender)await sender.replaceTrack(track);
    placeholder.hidden=true;stop.disabled=false;await catalog();if(run===session&&stream)await keepAwake();
  }finally{changing=false;if(!stream)start.disabled=false;}
}

async function catalog(){
  if(!stream)return;
  const track=stream.getVideoTracks()[0],settings=track.getSettings(),caps=track.getCapabilities?track.getCapabilities():{};
  const devices=(await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='videoinput'&&d.deviceId);
  if(!stream||stream.getVideoTracks()[0]!==track)return;
  const value={id:phoneId,name,activeId:cameraId,zoom:settings.zoom||1,torch:!!settings.torch,stabilization:'off',
    cameras:devices.map((d,i)=>({id:d.deviceId,label:d.label||'Camera '+(i+1),selectable:true,facing:d.deviceId===cameraId&&settings.facingMode==='user'?'Front':'Back',maxDigitalZoom:d.deviceId===cameraId?(caps.zoom?.max||1):1,flash:d.deviceId===cameraId&&!!caps.torch,eis:false,ois:false}))};
  if(channel?.readyState==='open')channel.send(JSON.stringify({kind:'catalog',catalog:value}));
}

async function control(values,run){
  if(run!==session||!stream)return;
  if(values.resolution)resolution=String(values.resolution).startsWith('1080')?'1080':'720';
  if(values.cameraId||values.resolution)await capture(values.cameraId||cameraId,run);
  if(run!==session)return;
  const track=stream?.getVideoTracks()[0],caps=track?.getCapabilities?track.getCapabilities():{};
  if(track&&values.zoom!==undefined&&caps.zoom)await track.applyConstraints({advanced:[{zoom:Math.max(caps.zoom.min,Math.min(caps.zoom.max,Number(values.zoom)))}]});
  if(track&&values.toggleTorch&&caps.torch)await track.applyConstraints({advanced:[{torch:!track.getSettings().torch}]});
  await catalog();
}

async function answer(offer,run){
  if(run!==session||!stream)return;
  closePeer();const pc=new RTCPeerConnection({iceServers:[]});peer=pc;
  state('connecting','Connecting…');
  pc.onconnectionstatechange=()=>{
    if(pc!==peer||run!==session)return;
    if(pc.connectionState==='connected'){remember('pairRoom',room);clearNotice();state('streaming','Connected');}
    else if(pc.connectionState==='disconnected')state('connecting','Reconnecting…');
    else if(pc.connectionState==='failed'){closePeer();discovery?.cancel();state('connecting','Waiting for LensioStudio');showError(new Error('Connection interrupted. Check the phone and PC are on the same Wi-Fi.'));}
  };
  pc.ondatachannel=event=>{
    if(pc!==peer)return;const dc=event.channel;channel=dc;
    dc.onopen=()=>{if(dc===channel)catalog().catch(showError);};
    dc.onmessage=event=>{
      if(dc!==channel||typeof event.data!=='string'||event.data.length>32768)return;
      try{const value=JSON.parse(event.data);if(value.kind==='control')controls=controls.then(()=>control(value.values,run)).catch(e=>{if(run===session)showError(e);});}catch(e){showError(e);}
    };
    dc.onclose=()=>{if(dc===channel&&run===session)state('connecting','Waiting for LensioStudio');};
  };
  stream.getTracks().forEach(track=>pc.addTrack(track,stream));
  await pc.setRemoteDescription(offer);await pc.setLocalDescription(await pc.createAnswer());await gatherIce(pc);
  if(pc!==peer||run!==session)return;
  const sender=pc.getSenders().find(s=>s.track?.kind==='video');
  if(sender){try{const p=sender.getParameters();p.degradationPreference='maintain-resolution';if(p.encodings?.length){p.encodings[0].maxBitrate=resolution==='1080'?16000000:8000000;p.encodings[0].maxFramerate=30;}await sender.setParameters(p);}catch{/* Unsupported Safari encoder options do not interrupt capture. */}}
  if(pc===peer&&run===session)discovery.answer({type:pc.localDescription.type,sdp:pc.localDescription.sdp});
}

start.addEventListener('click',async()=>{
  if(start.disabled||changing)return;clearNotice();
  if(!validRoom(room)){showError(new Error('Scan the QR in LensioStudio on your PC first.'));return;}
  discovery?.close();discovery=null;closePeer();controls=Promise.resolve();
  const run=++session;start.disabled=true;stop.disabled=false;state('connecting','Starting camera…');
  try{
    await capture(saved('camera'),run);if(run!==session)return;
    state('connecting','Waiting for LensioStudio');
    discovery=new AutoConnect({endpoint:'https://lensio-connect.pex-am.workers.dev',room,role:'phone',name,
      onRequest:async request=>{try{await answer(request.offer,run);}catch(e){if(run===session){showError(e);discovery?.cancel();state('connecting','Waiting for LensioStudio');}}},
      onGone:()=>{if(run!==session||peer?.connectionState==='connected')return;closePeer();state('connecting','Waiting for LensioStudio');},
      onStatus:text=>{if(run===session&&peer?.connectionState!=='connected'&&/unavailable|disconnected/i.test(text))state('connecting','Reconnecting…');}
    });
  }catch(e){if(run===session){await stopCamera();state('error','Unable to connect');showError(e);}}
});
stop.addEventListener('click',stopCamera);
document.addEventListener('visibilitychange',()=>{if(stream&&document.visibilityState==='visible')keepAwake();});
addEventListener('pagehide',()=>{session++;discovery?.close();closePeer();if(stream)stream.getTracks().forEach(t=>t.stop());releaseWake();});
