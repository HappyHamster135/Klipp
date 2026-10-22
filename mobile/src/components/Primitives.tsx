import { useEffect, useRef, type ReactNode } from 'react';
import { X } from 'lucide-react';
export function Dialog({title,children,onClose}:{title:string;children:ReactNode;onClose:()=>void}) {
  const ref=useRef<HTMLDialogElement>(null);
  useEffect(()=>{
    const dialog=ref.current,previous=document.activeElement;
    dialog?.showModal();
    return()=>{dialog?.close();if(previous instanceof HTMLElement&&previous.isConnected)previous.focus({preventScroll:true});};
  },[]);
  return <dialog ref={ref} className="dialog" onCancel={event=>{event.preventDefault();onClose();}} aria-label={title}><header><h2>{title}</h2><button className="icon-button" aria-label="Close dialog" onClick={onClose}><X/></button></header>{children}</dialog>;
}
export function Notice({children,onClose}:{children:ReactNode;onClose?:()=>void}) { return <div role="status" className="notice"><span>{children}</span>{onClose?<button className="icon-button" onClick={onClose} aria-label="Dismiss message"><X/></button>:null}</div>; }
export function Empty({title,children}:{title:string;children:ReactNode}) {return <section className="empty"><h2>{title}</h2><div>{children}</div></section>;}
