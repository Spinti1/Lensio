// No hosted signaling or media relay. A pairing code is a bounded, versioned SDP envelope.
export async function encodeCode(description) {
  const bytes = new TextEncoder().encode(JSON.stringify({v:1,type:description.type,sdp:description.sdp}));
  let packed=bytes,prefix='L1.';
  if(typeof CompressionStream!=='undefined') {
    packed=new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new CompressionStream('deflate'))).arrayBuffer());prefix='LZ.';
  }
  let binary='';for(const b of packed)binary+=String.fromCharCode(b);
  return prefix+btoa(binary).replaceAll('+','-').replaceAll('/','_').replace(/=+$/,'');
}
export async function decodeCode(input, expectedType) {
  let code=input.trim();
  if(code.startsWith('https://'))code=new URLSearchParams(new URL(code).hash.slice(1)).get('offer')||'';
  if(code.length>32000 || !/^L[1Z]\.[A-Za-z0-9_-]+$/.test(code))throw new Error('Paste a valid Lensio pairing code or pairing link.');
  const raw=atob(code.slice(3).replaceAll('-','+').replaceAll('_','/'));
  let bytes=Uint8Array.from(raw,c=>c.charCodeAt(0));
  if(code.startsWith('LZ.')) {
    if(typeof DecompressionStream==='undefined')throw new Error('Update Safari to use this pairing code.');
    const reader=new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate')).getReader();
    const chunks=[];let length=0;
    try {while(true){const part=await reader.read();if(part.done)break;length+=part.value.length;if(length>48000)throw new Error('Pairing code is too large.');chunks.push(part.value);}}
    finally {await reader.cancel();}
    bytes=new Uint8Array(length);let at=0;for(const c of chunks){bytes.set(c,at);at+=c.length;}
  }
  if(bytes.length>48000)throw new Error('Pairing code is too large.');
  const value=JSON.parse(new TextDecoder().decode(bytes));
  if(value.v!==1 || value.type!==expectedType || typeof value.sdp!=='string' || !value.sdp.startsWith('v=0'))throw new Error('This code belongs to the wrong pairing step.');
  return {type:value.type,sdp:value.sdp};
}
export function gatherIce(pc,timeout=12000) {
  if(pc.iceGatheringState==='complete')return Promise.resolve();
  return new Promise((resolve,reject)=>{
    const done=()=>{clearTimeout(timer);pc.removeEventListener('icegatheringstatechange',change);};
    const change=()=>{if(pc.iceGatheringState==='complete'){done();resolve();}};
    const timer=setTimeout(()=>{done();reject(new Error('Could not collect local network candidates. Try pairing again.'));},timeout);
    pc.addEventListener('icegatheringstatechange',change);
  });
}
