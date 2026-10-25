import type {Page,TestInfo} from '@playwright/test';
declare const process:{env:{KLIPP_QA_SCREENSHOT_DIR?:string}};

/** Optional synthetic screenshots go only to an explicitly selected QA folder. */
export async function capturePlaybackEvidence(page:Page,info:TestInfo,name:string){
  const directory=process.env.KLIPP_QA_SCREENSHOT_DIR;if(!directory)return;
  await page.screenshot({path:`${directory.replace(/[\\/]+$/,'')}/${info.project.name}-${name}.png`,fullPage:false,scale:'css'});
}
