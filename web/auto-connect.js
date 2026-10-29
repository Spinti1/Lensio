// A short-lived discovery socket carries SDP only. Video uses the existing direct peer.
export class AutoConnect {
  constructor({endpoint,role,name,onDevices,onRequest,onAnswer,onGone,onStatus}){
    Object.assign(this,{endpoint,role,name,onDevices,onRequest,onAnswer,onGone,onStatus});this.closed=false;this.timer=null;this.retry=null;this.socket=null;this.open();
  }
  open(){
    if(this.closed)return;
    const ws=new WebSocket(this.endpoint.replace(/^https:/,'wss:')+'/signal');this.socket=ws;
    ws.onopen=()=>{if(ws!==this.socket)return;this.onStatus('Looking for devices…');this.send({kind:'hello',role:this.role,name:this.name});this.timer=setInterval(()=>{if(this.socket?.readyState===WebSocket.OPEN)this.send({kind:'ping'});},25000);};
    ws.onmessage=async e=>{
      if(ws!==this.socket || typeof e.data!=='string'||e.data.length>56000)return;
      try{const v=JSON.parse(e.data);
        if(v.kind==='devices')this.onDevices?.(v.devices);
        else if(v.kind==='request')await this.onRequest?.(v);
        else if(v.kind==='answer')await this.onAnswer?.(v.answer);
        else if(v.kind==='gone')this.onGone?.();
        else if(v.kind==='error')this.onStatus(v.message);
      }catch(e){this.onStatus(e.message);this.cancel();}
    };
    ws.onclose=()=>{if(ws!==this.socket)return;clearInterval(this.timer);this.timer=null;this.onDevices?.([]);this.onGone?.();if(!this.closed){this.onStatus('Discovery disconnected · retrying…');this.retry=setTimeout(()=>this.open(),5000);}};
    ws.onerror=()=>this.onStatus('Discovery unavailable · use manual pairing or retry.');
  }
  send(value){if(this.socket?.readyState!==WebSocket.OPEN)throw new Error('Discovery is connecting. Try again in a moment.');this.socket.send(JSON.stringify(value));}
  request(id,offer){this.send({kind:'request',target:id,offer});}
  answer(answer){this.send({kind:'answer',answer});}
  cancel(){if(this.socket?.readyState===WebSocket.OPEN)this.send({kind:'cancel'});}
  close(){this.closed=true;clearInterval(this.timer);clearTimeout(this.retry);this.socket?.close();this.socket=null;}
}
