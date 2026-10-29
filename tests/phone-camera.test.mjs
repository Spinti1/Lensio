import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {runInNewContext} from 'node:vm';
import {webcrypto} from 'node:crypto';
const source=(await readFile(new URL('../web/app.js',import.meta.url),'utf8')).replace(/^import .*;\r?\n/gm,'');
const flush=()=>new Promise(resolve=>setImmediate(resolve));
function setup({paired=true,delayed=false}={}){
  const ids=['localVideo','start','stop','placeholder','message','state'];
  const elements=Object.fromEntries(ids.map(id=>[id,{id,disabled:id==='stop',hidden:id==='message',textContent:'',listeners:{},addEventListener(kind,fn){this.listeners[kind]=fn;},async play(){},async click(){await this.listeners.click();}}]));
  const storage=new Map(),media=[],tracks=[],peers=[],discoveries=[];let resolveMedia,wakeReleased=0;
  function track(id){const t={id,kind:'video',stopped:false,settings:{deviceId:id,facingMode:id==='front'?'user':'environment',zoom:1,torch:false},addEventListener(){},getSettings(){return this.settings;},getCapabilities(){return {zoom:{min:1,max:4},torch:true};},stop(){this.stopped=true;},async applyConstraints(c){Object.assign(this.settings,c.advanced[0]);}};tracks.push(t);return t;}
  class Discovery{constructor(options){this.options=options;discoveries.push(this);}answer(value){this.answerValue=value;}close(){this.closed=true;}cancel(){this.cancelled=true;}}
  class Peer{constructor(){this.connectionState='new';this.sender={track:null,parameters:{encodings:[{}]},getParameters(){return this.parameters;},async setParameters(p){this.parameters=p;},async replaceTrack(t){this.track=t;}};peers.push(this);}close(){this.closed=true;}addTrack(t){this.sender.track=t;}getSenders(){return [this.sender];}async setRemoteDescription(d){this.remoteDescription=d;}async createAnswer(){return {type:'answer',sdp:'v=0\r\ntest'};}async setLocalDescription(d){this.localDescription=d;}}
  const context={AutoConnect:Discovery,RTCPeerConnection:Peer,gatherIce:async()=>{},URLSearchParams,Uint8Array,crypto:webcrypto,
    document:{body:{dataset:{}},getElementById(id){assert(ids.includes(id),'Unexpected removed UI element '+id);return elements[id];},addEventListener(){},visibilityState:'visible'},
    location:{hash:paired?'#room='+'a'.repeat(64):'',pathname:'/',search:'?v=7'},history:{replaceState(){}},
    localStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value)},addEventListener(){},
    navigator:{userAgent:'iPhone',wakeLock:{async request(){return {addEventListener(){},async release(){wakeReleased++;}};}},mediaDevices:{
      async getUserMedia(options){media.push(options);if(delayed&&media.length===1)return new Promise(resolve=>{resolveMedia=()=>{const t=track('back');resolve({getTracks:()=>[t],getVideoTracks:()=>[t]});};});const t=track(options.video.deviceId?.exact||'back');return {getTracks:()=>[t],getVideoTracks:()=>[t]};},
      async enumerateDevices(){return [{kind:'videoinput',deviceId:'back',label:'Back camera'},{kind:'videoinput',deviceId:'front',label:'Front camera'}];}
    }}
  };
  runInNewContext(source,context);
  return {elements,storage,media,tracks,peers,discoveries,context,resolveMedia:()=>resolveMedia(),get wakeReleased(){return wakeReleased;}};
}
const page=setup();await page.elements.start.click();assert.equal(page.media.length,1);assert.equal(page.media[0].audio,false);assert.equal(page.media[0].video.frameRate.max,30);assert.equal(page.elements.start.disabled,true);assert.equal(page.elements.stop.disabled,false);
assert.equal(page.discoveries[0].options.room,'a'.repeat(64));assert.equal(page.discoveries[0].options.role,'phone');
await page.discoveries[0].options.onRequest({offer:{type:'offer',sdp:'v=0'}});const peer=page.peers[0];assert.equal(page.discoveries[0].answerValue.type,'answer');assert.equal(peer.sender.parameters.encodings[0].maxFramerate,30);
peer.connectionState='connected';peer.onconnectionstatechange();assert.equal(page.storage.get('lensio.pairRoom'),'a'.repeat(64));assert.equal(page.context.document.body.dataset.phase,'streaming');
const sent=[],channel={readyState:'open',send:value=>sent.push(JSON.parse(value))};peer.ondatachannel({channel});channel.onopen();await flush();assert.equal(sent[0].catalog.cameras.length,2);
channel.onmessage({data:JSON.stringify({kind:'control',values:{resolution:'1080p',cameraId:'front',zoom:2,toggleTorch:true}})});await flush();
assert.equal(page.media.at(-1).video.height.ideal,1080);assert.equal(page.media.at(-1).video.deviceId.exact,'front');assert(page.tracks[0].stopped);assert.equal(peer.sender.track.id,'front');assert.equal(peer.sender.track.settings.zoom,2);assert.equal(peer.sender.track.settings.torch,true);
await page.elements.stop.click();assert(peer.closed);assert(page.discoveries[0].closed);assert(page.tracks.every(t=>t.stopped));assert.equal(page.wakeReleased,1);assert.equal(page.elements.localVideo.srcObject,null);assert.equal(page.elements.start.disabled,false);assert.equal(page.elements.stop.disabled,true);
await page.elements.start.click();assert.equal(page.media.length,3);await page.elements.stop.click();
const unpaired=setup({paired:false});await unpaired.elements.start.click();assert.equal(unpaired.media.length,0);assert.match(unpaired.elements.message.textContent,/Scan the QR/);
const pending=setup({delayed:true});const opening=pending.elements.start.click();await flush();await pending.elements.stop.click();pending.resolveMedia();await opening;assert(pending.tracks[0].stopped);assert.equal(pending.discoveries.length,0);assert.equal(pending.elements.start.disabled,false);
console.log('PASS: minimal page start/stop/restart, QR association, SDP answer, catalog, desktop camera/resolution/zoom/torch controls, cleanup and stop during permission request');
