const clipIdPattern=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function clipViewerLink(id:string,pageUrl:string):string{
  if(!clipIdPattern.test(id))throw new Error('This clip cannot be shared.');
  const page=new URL(pageUrl);
  if(page.protocol!=='https:'&&page.protocol!=='http:')throw new Error('This clip cannot be shared here.');
  const link=new URL('./',page);
  link.username='';link.password='';link.search='';link.hash=`watch/${id.toLowerCase()}`;
  return link.href;
}
export interface ShareEnvironment{
  share?:(data:Pick<ShareData,'title'|'url'>)=>Promise<void>;
  copy?:(text:string)=>Promise<void>;
}
export async function shareClipLink(id:string,title:string,pageUrl:string,environment:ShareEnvironment){
  const url=clipViewerLink(id,pageUrl);
  if(environment.share){
    try{await environment.share({title,url});return{status:'shared' as const,url};}
    catch(error){if(error&&typeof error==='object'&&'name' in error&&error.name==='AbortError')return{status:'canceled' as const,url};}
  }
  if(environment.copy){
    try{await environment.copy(url);return{status:'copied' as const,url};}catch{/* Show a selectable link when clipboard access is unavailable. */}
  }
  return{status:'manual' as const,url};
}
