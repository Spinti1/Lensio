import assert from 'node:assert/strict';
import WebSocket from '../connect/node_modules/ws/wrapper.mjs';
const url='wss://lensio-connect.pex-am.workers.dev/signal';
function client(origin){
  const ws=new WebSocket(url,{origin});const queue=[],waiters=[];
  ws.on('message',b=>{const v=JSON.parse(b.toString());const index=waiters.findIndex(w=>w.kind===v.kind);if(index>=0){const w=waiters.splice(index,1)[0];clearTimeout(w.timer);w.resolve(v);}else queue.push(v);});
  const ready=new Promise((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});
  const receive=kind=>{const i=queue.findIndex(v=>v.kind===kind);if(i>=0)return Promise.resolve(queue.splice(i,1)[0]);return new Promise((resolve,reject)=>{const w={kind,resolve,timer:setTimeout(()=>reject(new Error('Timed out waiting for '+kind+'; messages='+JSON.stringify(queue))),15000)};waiters.push(w);});};
  return {ws,ready,receive,send:v=>ws.send(JSON.stringify(v))};
}
const host=client('http://127.0.0.1:45678'),phone=client('https://spinti1.github.io');
try{
  await Promise.all([host.ready,phone.ready]);await host.receive('welcome');const {id}=await phone.receive('welcome');
  host.send({kind:'hello',role:'host',name:'Test Windows'});phone.send({kind:'hello',role:'phone',name:'Test iPhone'});
  let found=false;for(let i=0;i<5&&!found;i++){const {devices}=await host.receive('devices');found=devices.some(d=>d.id===id);}assert(found);
  host.send({kind:'request',target:id,offer:{type:'offer',sdp:'v=0\r\na=test-offer\r\n'}});
  assert.equal((await phone.receive('request')).name,'Test Windows');
  phone.send({kind:'answer',answer:{type:'answer',sdp:'v=0\r\na=test-answer\r\n'}});assert.equal((await host.receive('answer')).id,id);
  phone.send({kind:'cancel'});assert.equal((await host.receive('gone')).id,id);
  console.log('PASS: public WSS discovery, device listing, offer/answer routing and cancel over the deployed service');
}finally{host.ws.close();phone.ws.close();}
