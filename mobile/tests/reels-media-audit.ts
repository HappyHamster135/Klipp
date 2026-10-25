interface AuditRequest{run:string;id:string;issued:string;range:string;bytes:number}
interface Request{url?:string;method?:string;headers:Record<string,string|string[]|undefined>}
interface Response{writeHead(status:number,headers?:Record<string,string|number>):void;end(body?:Uint8Array|string):void}
interface Server{listen(port:number,host:string,ready:()=>void):void;address():{port:number}|null;close(done:(error?:Error)=>void):void;closeAllConnections?():void}
export interface MediaAudit{origin:string;requests:AuditRequest[];close():Promise<void>}

/** Native WebKit media GETs bypass Playwright's page request observer. Count
 * actual HTTP traffic in this test-only, loopback-only synthetic media server. */
export async function createMediaAudit():Promise<MediaAudit>{
  const httpName='node:http',fsName='node:fs/promises';
  const http=await import(httpName) as {createServer(handler:(request:Request,response:Response)=>void):Server};
  const fs=await import(fsName) as {readFile(path:URL):Promise<Uint8Array>};
  const bytes=await fs.readFile(new URL('../.qa/test-video.mp4',import.meta.url)),requests:AuditRequest[]=[];
  const server=http.createServer((request,response)=>{
    const url=new URL(request.url??'/','http://127.0.0.1');
    if(url.pathname!=='/video.mp4'||!['GET','HEAD'].includes(request.method??'')){response.writeHead(404);response.end();return;}
    const range=typeof request.headers.range==='string'?request.headers.range:'';
    let start=0,end=bytes.length-1,status=200;
    if(range){
      const match=/^bytes=(\d+)-(\d*)$/.exec(range);
      if(!match){response.writeHead(416,{'Content-Range':`bytes */${bytes.length}`});response.end();return;}
      start=Number(match[1]);end=match[2]?Math.min(Number(match[2]),bytes.length-1):bytes.length-1;status=206;
      if(start>end||start>=bytes.length){response.writeHead(416,{'Content-Range':`bytes */${bytes.length}`});response.end();return;}
    }
    const length=end-start+1,headers:Record<string,string|number>={'Content-Type':'video/mp4','Content-Length':length,'Accept-Ranges':'bytes','Cache-Control':'private, no-store','Access-Control-Allow-Origin':'*'};
    if(status===206)headers['Content-Range']=`bytes ${start}-${end}/${bytes.length}`;
    if(request.method==='GET')requests.push({run:url.searchParams.get('run')??'',id:url.searchParams.get('clip')??'',issued:url.searchParams.get('issued')??'',range,bytes:length});
    response.writeHead(status,headers);response.end(request.method==='HEAD'?undefined:bytes.subarray(start,end+1));
  });
  await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));
  const address=server.address();if(!address)throw Error('Synthetic media server did not listen');
  return{origin:`http://127.0.0.1:${address.port}`,requests,close:()=>new Promise<void>((resolve,reject)=>{server.close(error=>error?reject(error):resolve());server.closeAllConnections?.();})};
}
