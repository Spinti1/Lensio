import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../connect/worker.js',import.meta.url),'utf8');
const {default:worker,NetworkRoom,networkKey,networkGroup}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const description=type=>({type,sdp:'v=0\r\na=fingerprint:test\r\n'});
const socket=()=>({attachment:null,messages:[],serializeAttachment(d){this.attachment=structuredClone(d);},deserializeAttachment(){return structuredClone(this.attachment);},send(s){this.messages.push(JSON.parse(s));},close(){this.closed=true;}});
const sockets=[];const room=new NetworkRoom({getWebSockets:()=>sockets.filter(s=>!s.closed),storage:{setAlarm:async()=>{}}});
function add(id,role){const ws=socket();ws.serializeAttachment({id,role:null,name:'',target:null,last:Date.now(),rateAt:Date.now(),rate:0});sockets.push(ws);send(ws,{kind:'hello',role,name:id});return ws;}
function send(ws,value){room.webSocketMessage(ws,JSON.stringify(value));}
const host=add('pc','host'),phone=add('iphone','phone'),stranger=add('other','host');
assert(host.messages.some(m=>m.kind==='devices'&&m.devices.some(d=>d.id==='iphone')));
send(host,{kind:'request',target:'iphone',offer:description('offer')});
assert.equal(phone.messages.at(-1).kind,'request');
send(stranger,{kind:'request',target:'iphone',offer:description('offer')});assert.equal(stranger.messages.at(-1).kind,'error');
send(stranger,{kind:'answer',answer:description('answer')});assert.equal(stranger.messages.at(-1).kind,'error');
send(phone,{kind:'answer',answer:description('answer')});assert.equal(host.messages.at(-1).kind,'answer');
send(phone,{kind:'cancel'});assert.equal(host.deserializeAttachment().target,null);assert.equal(phone.deserializeAttachment().target,null);
send(host,{kind:'request',target:'missing',offer:description('offer')});assert.equal(host.messages.at(-1).kind,'error');
send(host,{kind:'request',target:'iphone',offer:{type:'offer',sdp:'v=0'+'x'.repeat(49000)}});assert.equal(host.messages.at(-1).kind,'error');
send(host,{kind:'request',target:'iphone',offer:description('offer')});room.webSocketClose(phone);assert.equal(host.deserializeAttachment().target,null);
const abusive=add('spam','host');for(let i=0;i<30;i++)send(abusive,{kind:'ping'});assert.equal(abusive.messages.at(-1).kind,'error');
assert.equal((await worker.fetch(new Request('https://example.com/signal',{headers:{Upgrade:'websocket',Origin:'https://evil.example'}}),{})).status,403);
assert.equal((await worker.fetch(new Request('https://example.com/signal',{headers:{Upgrade:'websocket',Origin:'https://spinti1.github.io'}}),{})).status,503);
assert.equal((await worker.fetch(new Request('https://example.com/health'),{})).status,200);
assert.notEqual(await networkKey('192.0.2.1','secret1'),await networkKey('192.0.2.1','secret2'));
assert.notEqual(await networkKey('192.0.2.1','secret1'),await networkKey('192.0.2.2','secret1'));
assert.equal(networkGroup('2001:db8:abcd:1234::1'),networkGroup('2001:0db8:abcd:1234:1234:5678:9abc:def0'));
assert.notEqual(networkGroup('2001:db8:abcd:1234::1'),networkGroup('2001:db8:abcd:1235::1'));
console.log('PASS: discovery, scoped offer/answer, busy target rejection, cancel/close cleanup, message/SDP bounds, origin rejection and keyed network grouping');
