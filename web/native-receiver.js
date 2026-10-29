import {AutoConnect} from './auto-connect.js?v=6';
import {gatherIce} from './protocol.js';
import qrcode from './qrcode.js';
const params=new URLSearchParams(location.hash.slice(1)),token=params.get('token'),room=params.get('room'),hostProof=params.get('host');
const endpoint='https://lensio-connect.pex-am.workers.dev';
const session=crypto.randomUUID(),peers=new Map(),retryAt=new Map();let available=[],closed=false,discovery;
const post=value=>window.chrome?.webview?.postMessage(value);
const status=text=>post({kind:'status',text});
if(!token||!room||!/^[a-f0-9]{64}$/.test(room))throw new Error('Missing native receiver credentials.');
const link='https://spinti1.github.io/Lensio/?v=6#room='+room;
const qr=qrcode(0,'M');qr.addData(link);qr.make();post({kind:'qr',data:qr.createDataURL(4,16)});
async function bridge(path,data,headers={}){
  const response=await fetch(path,{method:'POST',headers:{Authorization:'Bearer '+token,'X-Session':session,...headers},body:data,cache:'no-store',signal:AbortSignal.timeout(7000)});
  if(!response.ok)throw new Error('Local camera receiver unavailable ('+response.status+').');
  return response.status===204?null:response.json();
}
function report(){const count=[...peers.values()].filter(p=>p.deviceId).length;status(count?'Web cameras connected: '+count:'Scan the QR on your phone, then press Start camera.');}
async function remove(id,notify=false){
  const p=peers.get(id);if(!p)return;peers.delete(id);p.stopped=true;clearTimeout(p.commandTimer);p.pc.close();p.worker?.terminate();
  try{await p.reader?.cancel();}catch{}
  if(p.deviceId)p.bridge('/disconnect',JSON.stringify({id:p.deviceId})).catch(()=>{});
  if(notify)discovery?.cancel(id);retryAt.set(id,Date.now()+5000);report();
}
async function capture(p,track){
  // Track processing is independent of DOM repaint and hidden-window animation timers.
  if(typeof MediaStreamTrackProcessor==='undefined'){
    try{p.worker=new Worker('./track-receiver.js',{type:'module'});p.worker.onmessage=e=>{if(e.data?.kind==='error'){status(e.data.message);remove(p.id,true);}};p.worker.postMessage({track,token,session:p.session,id:p.deviceId},[track]);return;}
    catch{p.worker?.terminate();throw new Error('This WebView2 runtime cannot process background video. Update Microsoft Edge WebView2 Runtime.');}
  }
  const processor=new MediaStreamTrackProcessor({track,maxBufferSize:1});p.reader=processor.readable.getReader();
  const canvas=new OffscreenCanvas(1,1),ctx=canvas.getContext('2d',{alpha:false});let frames=0,at=performance.now(),fps=30;
  try{while(!closed&&!p.stopped){
    const result=await p.reader.read();if(result.done)break;const frame=result.value;
    if(!p.deviceId){frame.close();continue;}
    try{if(canvas.width!==frame.displayWidth||canvas.height!==frame.displayHeight){canvas.width=frame.displayWidth;canvas.height=frame.displayHeight;}ctx.drawImage(frame,0,0);}finally{frame.close();}
    const blob=await canvas.convertToBlob({type:'image/jpeg',quality:0.94});
    if(p.stopped)break;
    await p.bridge('/frame',blob,{'X-Device':p.deviceId,'X-Width':String(canvas.width),'X-Height':String(canvas.height),'X-Fps':String(fps),'Content-Type':'image/jpeg'});
    frames++;const elapsed=performance.now()-at;if(elapsed>1000){fps=Math.max(1,Math.min(60,Math.round(frames*1000/elapsed)));at=performance.now();frames=0;}
  }}catch(e){if(!p.stopped){status(e.message);remove(p.id,true);}}
}
async function commands(p){
  if(p.stopped||!p.deviceId)return;
  try{const response=await p.bridge('/commands',JSON.stringify({id:p.deviceId}));for(const values of response.commands)if(p.channel?.readyState==='open')p.channel.send(JSON.stringify({kind:'control',values}));}
  catch(e){if(!p.stopped){status(e.message);remove(p.id,true);}return;}
  if(!p.stopped)p.commandTimer=setTimeout(()=>commands(p),200);
}
async function connect(device){
  if(peers.has(device.id)||peers.size>=4||Date.now()<(retryAt.get(device.id)||0))return;
  const pc=new RTCPeerConnection({iceServers:[]}),p={id:device.id,pc,session:crypto.randomUUID(),stopped:false,deviceId:null,track:null};peers.set(device.id,p);
  p.bridge=(path,data,headers={})=>bridge(path,data,{'X-Session':p.session,...headers});
  pc.addTransceiver('video',{direction:'recvonly'});p.channel=pc.createDataChannel('lensio-control');
  p.channel.onmessage=async event=>{
    try{
      if(p.stopped||typeof event.data!=='string'||event.data.length>32768)return;
      const value=JSON.parse(event.data);if(value.kind!=='catalog')return;const catalog=value.catalog;
      if(!catalog||!/^web:[a-zA-Z0-9-]{1,80}$/.test(catalog.id)||!Array.isArray(catalog.cameras)||catalog.cameras.length>32)throw new Error('Invalid phone camera information.');
      if(p.deviceId&&p.deviceId!==catalog.id)throw new Error('Phone identity changed.');
      if(!p.deviceId){await p.bridge('/register',JSON.stringify(catalog));if(p.stopped)return;p.deviceId=catalog.id;commands(p);report();if(p.track)capture(p,p.track).catch(e=>{status(e.message);remove(p.id,true);});}
      else await p.bridge('/catalog',JSON.stringify(catalog));
    }catch(e){status(e.message);remove(p.id,true);}
  };
  p.channel.onclose=()=>remove(p.id,true);
  pc.ontrack=e=>{p.track=e.track;if(p.deviceId)capture(p,e.track).catch(e=>{status(e.message);remove(p.id,true);});};
  pc.onconnectionstatechange=()=>{if(pc.connectionState==='failed'||pc.connectionState==='closed')remove(p.id,true);};
  try{await pc.setLocalDescription(await pc.createOffer());await gatherIce(pc);if(!p.stopped)discovery.request(device.id,{type:pc.localDescription.type,sdp:pc.localDescription.sdp});}
  catch(e){status(e.message);remove(p.id,true);}
}
discovery=new AutoConnect({endpoint,room,hostProof,role:'host',name:'LensioStudio',onDevices:devices=>{available=devices;for(const device of devices)connect(device);},
  onAnswer:async(answer,id)=>{const p=peers.get(id);if(p&&!p.stopped)await p.pc.setRemoteDescription(answer);},
  onGone:id=>{if(id)remove(id);},onStatus:status,
  onDiagnostic:text=>{if(text.startsWith('Discovery online')){report();post({kind:'ready',capture:typeof MediaStreamTrackProcessor==='function'?'track-processor':'worker-track-processor'});}}
});
const retries=setInterval(()=>{for(const device of available)connect(device);},5000);
addEventListener('pagehide',()=>{closed=true;clearInterval(retries);discovery.close();for(const id of [...peers.keys()])remove(id);});
