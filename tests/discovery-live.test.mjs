import assert from 'node:assert/strict';
import WebSocket from '../connect/node_modules/ws/wrapper.mjs';
import {createHash,randomBytes} from 'node:crypto';
const url='wss://lensio-connect.pex-am.workers.dev/signal';
function client(origin,query=''){
  const ws=new WebSocket(url+query,{origin});const queue=[],waiters=[];
  ws.on('message',b=>{const v=JSON.parse(b.toString());const index=waiters.findIndex(w=>w.kind===v.kind);if(index>=0){const w=waiters.splice(index,1)[0];clearTimeout(w.timer);w.resolve(v);}else queue.push(v);});
  const ready=new Promise((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});
  const receive=kind=>{const i=queue.findIndex(v=>v.kind===kind);if(i>=0)return Promise.resolve(queue.splice(i,1)[0]);return new Promise((resolve,reject)=>{const w={kind,resolve,timer:setTimeout(()=>reject(new Error('Timed out waiting for '+kind+'; messages='+JSON.stringify(queue))),15000)};waiters.push(w);});};
  return {ws,ready,receive,send:v=>ws.send(JSON.stringify(v))};
}
const host=client('http://127.0.0.1:45678'),phone=client('https://spinti1.github.io');
try{
  await Promise.all([host.ready,phone.ready]);const hostInfo=await host.receive('welcome');const phoneInfo=await phone.receive('welcome');const {id}=phoneInfo;
  assert.match(hostInfo.network,/^[a-f0-9]{8}$/);assert(['IPv4','IPv6'].includes(hostInfo.family));
  assert.equal(hostInfo.network,phoneInfo.network,'Two clients on this test path reached different discovery rooms');
  host.send({kind:'hello',role:'host',name:'Test Windows'});phone.send({kind:'hello',role:'phone',name:'Test iPhone'});
  let found=false;for(let i=0;i<5&&!found;i++){const {devices}=await host.receive('devices');found=devices.some(d=>d.id===id);}assert(found);
  host.send({kind:'request',target:id,offer:{type:'offer',sdp:'v=0\r\na=test-offer\r\n'}});
  assert.equal((await phone.receive('request')).name,'Test Windows');
  phone.send({kind:'answer',answer:{type:'answer',sdp:'v=0\r\na=test-answer\r\n'}});assert.equal((await host.receive('answer')).id,id);
  phone.send({kind:'cancel'});assert.equal((await host.receive('gone')).id,id);
  console.log('PASS: public WSS discovery, device listing, offer/answer routing and cancel over the deployed service');
}finally{host.ws.close();phone.ws.close();}
const proof=randomBytes(32).toString('hex'),room=createHash('sha256').update(proof).digest('hex');
const pc=client('http://127.0.0.1:45678','?room='+room+'&host='+proof);
const a=client('https://spinti1.github.io','?room='+room),b=client('https://spinti1.github.io','?room='+room),impostor=client('https://spinti1.github.io','?room='+room);
try{
  await Promise.all([pc.ready,a.ready,b.ready,impostor.ready]);await pc.receive('welcome');const ai=await a.receive('welcome'),bi=await b.receive('welcome');await impostor.receive('welcome');
  pc.send({kind:'hello',role:'host',name:'Test native PC'});a.send({kind:'hello',role:'phone',name:'Test Android'});b.send({kind:'hello',role:'phone',name:'Test iPhone'});
  impostor.send({kind:'hello',role:'host',name:'Phone cannot become PC'});assert.match((await impostor.receive('error')).message,/Only the paired PC/);
  for(const device of [ai,bi]){pc.send({kind:'request',target:device.id,offer:{type:'offer',sdp:'v=0\r\na=test-offer\r\n'}});}
  await a.receive('request');await b.receive('request');
  a.send({kind:'answer',answer:{type:'answer',sdp:'v=0\r\na=test-a\r\n'}});b.send({kind:'answer',answer:{type:'answer',sdp:'v=0\r\na=test-b\r\n'}});
  const received=[(await pc.receive('answer')).id,(await pc.receive('answer')).id];assert.deepEqual(received.sort(),[ai.id,bi.id].sort());
  pc.send({kind:'cancel',target:ai.id});await a.receive('gone');b.send({kind:'ping'});await b.receive('pong');
  console.log('PASS: private QR room, authenticated native PC, two simultaneous phones, isolated cancellation and rejection of phone-as-host');
}finally{for(const c of [pc,a,b,impostor])c.ws.close();}
