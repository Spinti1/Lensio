// Pairing only. WebRTC camera/audio payloads are never accepted by this service.
const allowedOrigin=origin=>origin==='https://spinti1.github.io'||/^http:\/\/127\.0\.0\.1:[1-9][0-9]{0,4}$/.test(origin||'');
const encoder=new TextEncoder();
export function networkGroup(ip){
  if(!ip.includes(':'))return ip;
  const pieces=ip.toLowerCase().split('::');
  const left=pieces[0]?pieces[0].split(':'):[],right=pieces.length>1&&pieces[1]?pieces[1].split(':'):[];
  const full=pieces.length>1?[...left,...Array(Math.max(0,8-left.length-right.length)).fill('0'),...right]:left;
  // Residential IPv6 normally gives each phone its own address in a common /64.
  return full.slice(0,4).map(p=>p.padStart(4,'0')).join(':')+'::/64';
}
export async function networkKey(ip,salt){
  const key=await crypto.subtle.importKey('raw',encoder.encode(salt),{name:'HMAC',hash:'SHA-256'},false,['sign']);
  return Array.from(new Uint8Array(await crypto.subtle.sign('HMAC',key,encoder.encode(ip))),b=>b.toString(16).padStart(2,'0')).join('');
}
export default {
  async fetch(request,env){
    const url=new URL(request.url);
    if(url.pathname==='/health')return Response.json({service:'Lensio Connect',version:1,mediaRelay:false},{headers:{'cache-control':'no-store'}});
    if(url.pathname!=='/signal'||request.headers.get('Upgrade')?.toLowerCase()!=='websocket')return new Response('Not found',{status:404});
    if(!allowedOrigin(request.headers.get('Origin')))return new Response('Forbidden',{status:403});
    const ip=request.headers.get('CF-Connecting-IP');
    if(!ip||!env.DISCOVERY_SALT)return new Response('Service unavailable',{status:503});
    const digest=await networkKey(networkGroup(ip),env.DISCOVERY_SALT);
    // No IP, device name or SDP is written to storage or application logs.
    return env.NETWORKS.get(env.NETWORKS.idFromName(digest)).fetch(new Request('https://internal/signal',{headers:{Upgrade:'websocket','X-Network-Hint':digest.slice(0,8),'X-Address-Family':ip.includes(':')?'IPv6':'IPv4'}}));
  }
};
export class NetworkRoom {
  constructor(ctx){this.ctx=ctx;}
  sockets(){return this.ctx.getWebSockets();}
  info(ws){try{return ws.deserializeAttachment();}catch{return null;}}
  send(ws,value){try{ws.send(JSON.stringify(value));}catch{}}
  find(id){return this.sockets().find(ws=>this.info(ws)?.id===id);}
  save(ws,data){ws.serializeAttachment(data);}
  async fetch(request){
    if(this.sockets().length>=32)return new Response('Network device limit reached',{status:429});
    const pair=new WebSocketPair(),client=pair[0],server=pair[1];
    this.ctx.acceptWebSocket(server);
    this.save(server,{id:crypto.randomUUID(),role:null,name:'',target:null,last:Date.now(),rateAt:Date.now(),rate:0});
    this.send(server,{kind:'welcome',id:this.info(server).id,network:request.headers.get('X-Network-Hint'),family:request.headers.get('X-Address-Family')});
    await this.ctx.storage.setAlarm(Date.now()+60000);
    return new Response(null,{status:101,webSocket:client});
  }
  list(){
    const phones=this.sockets().map(ws=>this.info(ws)).filter(d=>d?.role==='phone'&&!d.target).map(d=>({id:d.id,name:d.name}));
    for(const ws of this.sockets())if(this.info(ws)?.role==='host')this.send(ws,{kind:'devices',devices:phones});
  }
  unpair(ws){
    const data=this.info(ws);if(!data)return;
    const other=data.target&&this.find(data.target);
    if(other){const d=this.info(other);if(d?.target===data.id){d.target=null;this.save(other,d);this.send(other,{kind:'gone',id:data.id});}}
    data.target=null;this.save(ws,data);
  }
  webSocketMessage(ws,message){
    try{
      if(typeof message!=='string'||message.length>56000)throw new Error('Invalid message');
      const data=this.info(ws);if(!data)throw new Error('Unknown session');
      const now=Date.now();if(now-data.rateAt>10000){data.rate=0;data.rateAt=now;}
      if(++data.rate>24)throw new Error('Message limit exceeded');data.last=now;this.save(ws,data);
      const value=JSON.parse(message);
      if(value.kind==='ping'){this.send(ws,{kind:'pong'});return;}
      if(value.kind==='hello'){
        if(data.role||!['host','phone'].includes(value.role))throw new Error('Invalid role');
        data.role=value.role;data.name=typeof value.name==='string'?value.name.replace(/[\u0000-\u001f]/g,'').slice(0,60):'';
        if(!data.name)data.name=data.role==='host'?'Windows Studio':'iPhone';
        this.save(ws,data);this.list();return;
      }
      if(!data.role)throw new Error('Register first');
      if(value.kind==='request'){
        if(data.role!=='host'||data.target)throw new Error('Receiver busy');
        const target=this.find(value.target),phone=target&&this.info(target);
        if(!phone||phone.role!=='phone'||phone.target)throw new Error('Phone unavailable');
        if(!validDescription(value.offer,'offer'))throw new Error('Invalid offer');
        data.target=phone.id;phone.target=data.id;this.save(ws,data);this.save(target,phone);
        this.send(target,{kind:'request',id:data.id,name:data.name,offer:value.offer});this.list();return;
      }
      if(value.kind==='answer'){
        if(data.role!=='phone'||!data.target||!validDescription(value.answer,'answer'))throw new Error('Invalid answer');
        const target=this.find(data.target);if(!target||this.info(target)?.target!==data.id)throw new Error('Receiver disconnected');
        this.send(target,{kind:'answer',id:data.id,answer:value.answer});return;
      }
      if(value.kind==='cancel'){this.unpair(ws);this.list();return;}
      throw new Error('Unsupported message');
    }catch(e){this.send(ws,{kind:'error',message:e.message});}
  }
  webSocketClose(ws){this.unpair(ws);try{ws.close();}catch{}this.list();}
  webSocketError(ws){this.webSocketClose(ws);}
  async alarm(){
    for(const ws of this.sockets()){const d=this.info(ws);if(!d||Date.now()-d.last>90000){this.unpair(ws);try{ws.close(1000,'Discovery expired');}catch{}}}
    this.list();if(this.sockets().length)await this.ctx.storage.setAlarm(Date.now()+60000);
  }
}
export function validDescription(value,type){return value&&value.type===type&&typeof value.sdp==='string'&&value.sdp.startsWith('v=0')&&value.sdp.length<=48000;}
