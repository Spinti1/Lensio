// Used by runtimes that expose MediaStreamTrackProcessor only in dedicated workers.
self.onmessage=async({data})=>{
  try{
    const {track,token,session,id}=data;
    const reader=new MediaStreamTrackProcessor({track,maxBufferSize:1}).readable.getReader();
    const canvas=new OffscreenCanvas(1,1),ctx=canvas.getContext('2d',{alpha:false});let fps=30,count=0,at=performance.now();
    while(true){
      const next=await reader.read();if(next.done)break;const frame=next.value;
      try{if(canvas.width!==frame.displayWidth||canvas.height!==frame.displayHeight){canvas.width=frame.displayWidth;canvas.height=frame.displayHeight;}ctx.drawImage(frame,0,0);}finally{frame.close();}
      const blob=await canvas.convertToBlob({type:'image/jpeg',quality:0.94});
      const response=await fetch('/frame',{method:'POST',headers:{Authorization:'Bearer '+token,'X-Session':session,'X-Device':id,'X-Width':String(canvas.width),'X-Height':String(canvas.height),'X-Fps':String(fps),'Content-Type':'image/jpeg'},body:blob,signal:AbortSignal.timeout(7000)});
      if(!response.ok)throw new Error('Local video receiver is unavailable.');
      count++;const elapsed=performance.now()-at;if(elapsed>1000){fps=Math.max(1,Math.min(60,Math.round(count*1000/elapsed)));count=0;at=performance.now();}
    }
  }catch(e){self.postMessage({kind:'error',message:e.message});}
};
