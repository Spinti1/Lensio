import {encodeCode,decodeCode,gatherIce} from './protocol.js';
import {AutoConnect} from './auto-connect.js?v=6';
const $=id=>document.getElementById(id);
const local=$('localVideo'),remote=$('remoteVideo');
const receiver=location.pathname==='/receiver' && location.hostname==='127.0.0.1';
const params=new URLSearchParams(location.hash.slice(1)),token=params.get('token');
const scannedRoom=params.get('room');
const pairRoom=/^[a-f0-9]{64}$/.test(scannedRoom||'')?scannedRoom:recalled('pairRoom','');
const PUBLIC_URL='https://spinti1.github.io/Lensio/';
const receiverSession=Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');
let peer,channel,stream,wake,stopped=true,bridgeId,postBusy=false,frameHandle,pollTimer;
let generation=0,changing=false,deviceId='',catalog,frames=0,reportedFps=30,statsAt=performance.now();
let discovery=null,discoveryEndpoint='',pendingRequest=null;
const DEFAULT_DISCOVERY='https://lensio-connect.pex-am.workers.dev';
const state=text=>$('state').textContent=text;
function error(e){$('message').textContent=e?.message||String(e);}
function remember(key,value){try{localStorage.setItem('lensio.'+key,value);}catch{}}
function recalled(key,fallback){try{return localStorage.getItem('lensio.'+key)||fallback;}catch{return fallback;}}
function identity(){let id=recalled('id','');if(!id){id=crypto.randomUUID?crypto.randomUUID():Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');remember('id',id);}return 'web:'+id;}
const phoneId=identity();
$('phoneName').value=recalled('name',/iPhone|iPad/.test(navigator.userAgent)?'iPhone':'Web camera');
$('resolution').value=recalled('resolution','720');
$('phoneName').addEventListener('change',()=>{remember('name',$('phoneName').value);sendCatalog().catch(error);});
async function bridge(path,data,headers={}) {
  const response=await fetch(path,{method:'POST',headers:{Authorization:'Bearer '+token,'X-Session':receiverSession,...headers},body:data,cache:'no-store',signal:typeof AbortSignal!=='undefined' && AbortSignal.timeout?AbortSignal.timeout(8000):undefined});
  if(!response.ok)throw new Error(response.status===401?'Receiver expired. Reopen iPhone / Web from LensioStudio.':'LensioStudio receiver: HTTP '+response.status);
  return response.status===204?null:response.json();
}
async function keepAwake(){try{if(!wake && navigator.wakeLock && document.visibilityState==='visible'){wake=await navigator.wakeLock.request('screen');wake.addEventListener('release',()=>{wake=null;});}}catch{}}
async function releaseWake(){if(wake){await wake.release().catch(()=>{});wake=null;}}
function closePeer(){generation++;if(peer)peer.close();peer=null;channel=null;clearTimeout(pollTimer);pollTimer=null;bridgeId=null;if(frameHandle){if(remote.cancelVideoFrameCallback)remote.cancelVideoFrameCallback(frameHandle);else cancelAnimationFrame(frameHandle);frameHandle=null;}remote.srcObject=null;}
async function stopCamera(){stopped=true;discovery?.close();discovery=null;pendingRequest=null;$('autoRequest').hidden=true;closePeer();if(stream)stream.getTracks().forEach(t=>t.stop());stream=null;local.srcObject=null;await releaseWake();$('placeholder').hidden=false;$('start').disabled=false;$('stop').disabled=true;$('connect').disabled=true;$('answerSection').hidden=true;state('Camera stopped');}
async function startCamera(id=deviceId){
  if(changing)return;changing=true;
  try {
    if(!navigator.mediaDevices?.getUserMedia)throw new Error('Open this page in Safari over HTTPS to use the camera.');
    const height=$('resolution').value==='1080'?1080:720;
    // Release the old track before opening another sensor. iOS can reject two simultaneous cameras.
    if(stream){stream.getTracks().forEach(t=>t.stop());stream=null;}
    const video={width:{ideal:height*16/9},height:{ideal:height},frameRate:{ideal:30,max:30}};
    if(id)video.deviceId={exact:id};else video.facingMode={ideal:'environment'};
    try{stream=await navigator.mediaDevices.getUserMedia({video,audio:false});}
    catch(e){if(id && (e.name==='OverconstrainedError'||e.name==='NotFoundError')){delete video.deviceId;video.facingMode={ideal:'environment'};stream=await navigator.mediaDevices.getUserMedia({video,audio:false});}else throw e;}
    const track=stream.getVideoTracks()[0];track.addEventListener('ended',()=>{state('Camera interrupted');error(new Error('Camera access ended. Keep Safari open, then press Start camera.'));$('start').disabled=false;$('connect').disabled=true;});
    deviceId=track.getSettings().deviceId||'';remember('camera',deviceId);remember('resolution',$('resolution').value);
    local.srcObject=stream;await local.play();
    if(peer){const sender=peer.getSenders().find(s=>s.track?.kind==='video');if(sender)await sender.replaceTrack(track);}
    stopped=false;$('placeholder').hidden=true;$('start').disabled=true;$('stop').disabled=false;$('connect').disabled=false;
    const settings=track.getSettings();$('format').textContent=`${settings.width||'?'} × ${settings.height||'?'}`;
    await sendCatalog();await keepAwake();state(channel?.readyState==='open'?'Streaming':'Camera ready');$('message').textContent='';
    if(!discovery && discoveryEndpoint)startDiscovery();
  } catch(e){$('start').disabled=false;$('stop').disabled=!stream;$('connect').disabled=!stream;error(e);throw e;}
  finally{changing=false;}
}
async function sendCatalog(){
  if(!stream)return;
  const track=stream.getVideoTracks()[0],settings=track.getSettings(),caps=track.getCapabilities?track.getCapabilities():{};
  const devices=(await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='videoinput' && d.deviceId);
  const selected=deviceId;$('cameras').replaceChildren();
  devices.forEach((d,i)=>{const option=document.createElement('option');option.value=d.deviceId;option.textContent=d.label||'Camera '+(i+1);$('cameras').append(option);});
  $('cameras').value=selected;
  catalog={id:phoneId,name:($('phoneName').value.trim()||'iPhone').slice(0,60),activeId:selected,zoom:settings.zoom||1,torch:!!settings.torch,stabilization:'off',
    cameras:devices.map((d,i)=>({id:d.deviceId,label:d.label||'Camera '+(i+1),selectable:true,facing:d.deviceId===selected && settings.facingMode==='user'?'Front':'Back',maxDigitalZoom:d.deviceId===selected?(caps.zoom?.max||1):1,flash:d.deviceId===selected && !!caps.torch,eis:false,ois:false}))};
  if(channel?.readyState==='open')channel.send(JSON.stringify({kind:'catalog',catalog}));
}
async function applyControl(values){
  if(values.resolution){$('resolution').value=String(values.resolution).startsWith('1080')?'1080':'720';}
  if(values.cameraId || values.resolution)await startCamera(values.cameraId||deviceId);
  const track=stream?.getVideoTracks()[0],caps=track?.getCapabilities?track.getCapabilities():{};
  if(track && values.zoom!==undefined && caps.zoom)await track.applyConstraints({advanced:[{zoom:Math.max(caps.zoom.min,Math.min(caps.zoom.max,Number(values.zoom)))}]});
  if(track && values.toggleTorch && caps.torch)await track.applyConstraints({advanced:[{torch:!track.getSettings().torch}]});
  await sendCatalog();
}
function makePeer(){
  const pc=new RTCPeerConnection({iceServers:[]});peer=pc;
  pc.addEventListener('connectionstatechange',()=>{
    if(pc!==peer)return;
    const s=pc.connectionState;state(s==='connected'?'Streaming':s==='failed'?'Connection failed':s);
    if(s==='connected'){if(!receiver)$('pairing').open=false;if(/^[a-f0-9]{64}$/.test(pairRoom||''))remember('pairRoom',pairRoom);$('message').textContent='';}
    if(s==='failed'){error(new Error('Connection failed. Check both devices are on the same Wi-Fi, without client isolation, and create a new pairing code.'));if(receiver && bridgeId)bridge('/disconnect',JSON.stringify({id:bridgeId})).catch(()=>{});}
  });
  return pc;
}
function bindChannel(dc){
  channel=dc;dc.onopen=()=>{if(dc===channel && !receiver)sendCatalog().catch(error);};
  dc.onmessage=async event=>{
    try{
      if(dc!==channel || typeof event.data!=='string' || event.data.length>32768)return;
      const value=JSON.parse(event.data);
      if(!receiver && value.kind==='control'){await applyControl(value.values);return;}
      if(receiver && value.kind==='catalog'){
        const incoming=value.catalog;
        if(!incoming || !/^web:[a-zA-Z0-9-]{1,80}$/.test(incoming.id) || !Array.isArray(incoming.cameras) || incoming.cameras.length>32)throw new Error('Invalid camera information.');
        if(bridgeId && bridgeId!==incoming.id)throw new Error('Phone identity changed. Pair again.');
        if(!bridgeId){await bridge('/register',JSON.stringify(incoming));if(dc!==channel)return;bridgeId=incoming.id;pollCommands();}
        else await bridge('/catalog',JSON.stringify(incoming));
      }
    }catch(e){error(e);}
  };
  dc.onclose=()=>{if(dc!==channel)return;state('Disconnected');if(receiver && bridgeId){const id=bridgeId;bridgeId=null;bridge('/disconnect',JSON.stringify({id})).catch(()=>{});}};
}
async function createAnswer(autoOffer=null){
  if(!stream)await startCamera();
  const description=autoOffer||await decodeCode($('offer').value,'offer');closePeer();
  const pc=makePeer();pc.ondatachannel=e=>bindChannel(e.channel);
  stream.getTracks().forEach(t=>pc.addTrack(t,stream));
  await pc.setRemoteDescription(description);
  await pc.setLocalDescription(await pc.createAnswer());await gatherIce(pc);
  // Retain camera resolution during congestion; let the browser use its hardware video encoder.
  const sender=pc.getSenders().find(s=>s.track?.kind==='video');
  if(sender){try{const p=sender.getParameters();p.degradationPreference='maintain-resolution';if(p.encodings?.length){p.encodings[0].maxBitrate=$('resolution').value==='1080'?16000000:8000000;p.encodings[0].maxFramerate=30;}await sender.setParameters(p);}catch{/* Some Safari versions do not expose encoder parameters. */}}
  if(autoOffer){discovery.answer({type:pc.localDescription.type,sdp:pc.localDescription.sdp});state('Connecting…');}
  else {$('answer').value=await encodeCode(pc.localDescription);$('answerSection').hidden=false;state('Return code ready');}
}
async function createOffer(autoTarget=null){
  if(bridgeId)await bridge('/disconnect',JSON.stringify({id:bridgeId})).catch(()=>{});
  closePeer();const pc=makePeer();pc.addTransceiver('video',{direction:'recvonly'});bindChannel(pc.createDataChannel('lensio-control'));
  pc.ontrack=async e=>{remote.srcObject=e.streams[0]||new MediaStream([e.track]);await remote.play();pumpFrames();};
  await pc.setLocalDescription(await pc.createOffer());await gatherIce(pc);
  if(autoTarget){discovery.request(autoTarget,{type:pc.localDescription.type,sdp:pc.localDescription.sdp});state('Accept on iPhone');}
  else {const code=await encodeCode(pc.localDescription);$('invite').value=PUBLIC_URL+'#offer='+code;$('inviteSection').hidden=false;$('remoteAnswer').value='';state('Waiting for iPhone');}
}
function startDiscovery(){
  if(discovery || !discoveryEndpoint || (!receiver&&!stream))return;
  $('autoPanel').hidden=false;
  discovery=new AutoConnect({endpoint:discoveryEndpoint,room:/^[a-f0-9]{64}$/.test(pairRoom||'')?pairRoom:undefined,role:receiver?'host':'phone',name:receiver?'LensioStudio on Windows':$('phoneName').value,
    onStatus:text=>$('autoStatus').textContent=text,
    onDiagnostic:text=>$('autoDiagnostic').textContent=text,
    onDevices:devices=>{
      if(!receiver)return;$('autoDevices').replaceChildren();
      $('autoStatus').textContent=devices.length?'Select your phone. Accept the request on its screen.':'Waiting for a phone. Open LensioCam Web in Safari and press Start camera.';
      for(const d of devices){const b=document.createElement('button');b.textContent='Connect '+d.name;b.className='primary';b.onclick=async()=>{b.disabled=true;try{discovery.cancel();await createOffer(d.id);}catch(e){error(e);b.disabled=false;}};$('autoDevices').append(b);}
    },
    onRequest:async request=>{
      if(/^[a-f0-9]{64}$/.test(pairRoom||'')){$('autoStatus').textContent='Connecting to your paired LensioStudio…';await createAnswer(request.offer);return;}
      pendingRequest=request;$('autoRequest').hidden=false;$('autoHost').textContent=request.name;
      $('autoStatus').textContent='A Windows receiver wants to use this camera. Only accept your own PC.';
    },
    onAnswer:async answer=>{if(!peer)throw new Error('Pairing expired. Select the phone again.');await peer.setRemoteDescription(answer);state('Connecting…');},
    onGone:()=>{if(peer?.connectionState==='connected')return;pendingRequest=null;$('autoRequest').hidden=true;closePeer();state('Waiting for device');}
  });
}
action('acceptAuto',async()=>{const request=pendingRequest;if(!request)throw new Error('Pairing request expired.');pendingRequest=null;$('autoRequest').hidden=true;await createAnswer(request.offer);});
action('rejectAuto',async()=>{pendingRequest=null;$('autoRequest').hidden=true;discovery.cancel();});
async function loadDiscovery(){
  $('autoPanel').hidden=false;
  discoveryEndpoint=DEFAULT_DISCOVERY;
  try{
    const response=await fetch('./config.json?v=6',{cache:'no-store'});
    if(!response.ok)throw new Error('Discovery configuration could not be loaded.');
    const config=await response.json();
    if(typeof config.discoveryEndpoint!=='string'||!/^https:\/\/[a-z0-9-]+\.[a-z0-9-]+\.workers\.dev$/.test(config.discoveryEndpoint))throw new Error('Invalid discovery configuration.');
    discoveryEndpoint=config.discoveryEndpoint;
  }catch{ $('message').textContent='Using built-in discovery configuration. Reload this page if discovery does not start.'; }
  $('autoStatus').textContent=receiver?'Looking for devices…':'Press Start camera to become visible to LensioStudio.';
  if(receiver)$('newPair').textContent='Manual pairing (fallback)';else $('pairing').open=false;
  try{startDiscovery();}catch(e){$('autoStatus').textContent='Discovery could not start: '+e.message;}
}
async function pollCommands(){
  if(!bridgeId)return;const id=bridgeId,run=generation;
  try{const values=await bridge('/commands',JSON.stringify({id}));if(run!==generation)return;for(const command of values.commands)if(channel?.readyState==='open')channel.send(JSON.stringify({kind:'control',values:command}));}
  catch(e){error(e);closePeer();return;}
  if(bridgeId && run===generation)pollTimer=setTimeout(pollCommands,200);
}
const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d',{alpha:false});
function pumpFrames(){
  const run=generation;
  function tick(now){
    if(run!==generation || !peer)return;
    frameHandle=remote.requestVideoFrameCallback?remote.requestVideoFrameCallback(tick):requestAnimationFrame(tick);
    if(postBusy || !bridgeId || !remote.videoWidth || now-lastFrame<30)return;
    lastFrame=now;postBusy=true;
    canvas.width=remote.videoWidth;canvas.height=remote.videoHeight;ctx.drawImage(remote,0,0);
    const id=bridgeId,width=canvas.width,height=canvas.height;
    canvas.toBlob(async blob=>{
      try{if(!blob || run!==generation)return;
        await bridge('/frame',blob,{'X-Device':id,'X-Width':String(width),'X-Height':String(height),'X-Fps':String(reportedFps),'Content-Type':'image/jpeg'});
        frames++;const elapsed=performance.now()-statsAt;if(elapsed>1000){reportedFps=Math.max(1,Math.min(60,Math.round(frames*1000/elapsed)));$('receiverStats').textContent=`${width} × ${height} · ${reportedFps} fps`;frames=0;statsAt=performance.now();}
      }catch(e){error(e);closePeer();}finally{postBusy=false;}
    },'image/jpeg',0.94);
  }
  let lastFrame=0;frameHandle=remote.requestVideoFrameCallback?remote.requestVideoFrameCallback(tick):requestAnimationFrame(tick);
}
function action(id,fn){$(id).addEventListener('click',async()=>{const b=$(id);b.disabled=true;$('message').textContent='';try{await fn();}catch(e){error(e);}finally{b.disabled=(id==='start' && !!stream);}});}
async function copy(id){try{await navigator.clipboard.writeText($(id).value);$('message').textContent='Copied. Paste it on your other device.';}catch{$(id).focus();$(id).select();$('message').textContent='Copy the selected text and paste it on your other device.';}}
action('start',()=>startCamera(recalled('camera','')));action('stop',stopCamera);action('connect',createAnswer);action('newPair',createOffer);
action('finish',async()=>{if(!peer)throw new Error('Create a pairing link first.');await peer.setRemoteDescription(await decodeCode($('remoteAnswer').value,'answer'));state('Connecting…');});
action('copyInvite',()=>copy('invite'));action('copyAnswer',()=>copy('answer'));
$('cameras').addEventListener('change',()=>startCamera($('cameras').value).catch(()=>{}));
$('resolution').addEventListener('change',()=>{remember('resolution',$('resolution').value);if(stream)startCamera().catch(()=>{});});
document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible'){if(!stopped)keepAwake();}else if(peer)error(new Error(receiver?'Keep this receiver window visible to maintain frame rate.':'Keep Safari visible and your screen on while streaming.'));});
addEventListener('pagehide',()=>{discovery?.close();if(receiver && bridgeId)fetch('/disconnect',{method:'POST',headers:{Authorization:'Bearer '+token,'X-Session':receiverSession},body:JSON.stringify({id:bridgeId}),keepalive:true}).catch(()=>{});closePeer();if(stream)stream.getTracks().forEach(t=>t.stop());releaseWake();});
if(receiver){$('sender').hidden=true;$('receiver').hidden=false;$('heading').textContent='Your iPhone. In your scene.';$('subtitle').textContent='Pair once for this session. LensioStudio handles composition, filters and virtual camera output.';if(!token)error(new Error('Open this receiver from LensioStudio.'));}
else {const offer=params.get('offer');if(offer)$('offer').value=offer;if(offer||scannedRoom)history.replaceState(null,'',location.pathname+location.search);
  if(pairRoom){$('start').textContent='Start camera & connect';$('subtitle').textContent='Your camera connects automatically to the PC whose QR you scanned. Keep this page open and your phone screen on.';$('pairing').hidden=true;}
}
loadDiscovery();
