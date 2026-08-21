(() => {
  if (window.__fingerprintSingleDeleteV2) return;
  window.__fingerprintSingleDeleteV2 = true;
  const API='http://localhost:5140';
  const token=()=>localStorage.getItem('of_token')||'';
  const deletedKey=(finger,pos,seq)=>`fpdel:${finger}|${pos}|${seq}`;

  function css(){
    if(document.getElementById('fpd-css-v2'))return;
    const s=document.createElement('style');s.id='fpd-css-v2';
    s.textContent=`.fp-del{position:absolute;right:7px;top:7px;z-index:5;width:28px;height:28px;border:1px solid #efb1aa;border-radius:7px;background:rgba(255,248,247,.96);color:#a2281b;font-size:14px;display:grid;place-items:center;opacity:0;transform:scale(.92);transition:.16s;cursor:pointer}.gallery-item:hover .fp-del,.gallery-item:focus-within .fp-del{opacity:1;transform:scale(1)}.gallery-item.fp-removing{pointer-events:none;animation:fpout .38s ease forwards}@keyframes fpout{0%{opacity:1;transform:translateY(0) scale(1);max-height:240px}65%{opacity:.25;transform:translateY(-8px) scale(.97);max-height:160px}100%{opacity:0;transform:translateY(-18px) scale(.92);max-height:0;margin:0;padding:0;border-width:0}}.fp-modal{position:fixed;inset:0;z-index:9999;background:rgba(15,23,42,.46);display:grid;place-items:center;padding:20px}.fp-card{width:min(430px,92vw);background:#fff;border-radius:16px;box-shadow:0 24px 80px rgba(0,0,0,.28);overflow:hidden;animation:fpin .18s ease-out}@keyframes fpin{from{opacity:0;transform:translateY(8px) scale(.98)}to{opacity:1;transform:none}}.fp-head{padding:18px 20px 10px;display:flex;gap:12px}.fp-icon{width:42px;height:42px;border-radius:12px;display:grid;place-items:center;background:#fff0ed;color:#a2281b;font-size:20px}.fp-title{font-size:17px;font-weight:800;color:#172033}.fp-text{margin-top:4px;font-size:13px;color:#667085;line-height:1.5}.fp-actions{display:flex;justify-content:flex-end;gap:8px;padding:16px 20px 20px}.fp-cancel,.fp-confirm{border-radius:9px;padding:9px 14px;font:inherit;font-weight:700;cursor:pointer}.fp-cancel{border:1px solid #d0d7e2;background:#fff;color:#344054}.fp-confirm{border:1px solid #b42318;background:#b42318;color:#fff}.fp-confirm:disabled{opacity:.6;cursor:not-allowed}`;document.head.appendChild(s)
  }
  function deletedKeys(){const s=new Set();for(let i=0;i<localStorage.length;i++){const k=localStorage.key(i);if(k&&k.startsWith('fpdel:'))s.add(k.slice(6))}return s}
  function patchFetch(){
    if(window.__fpFetchPatched)return;window.__fpFetchPatched=true;
    const original=window.fetch.bind(window);
    window.fetch=async(...args)=>{
      const r=await original(...args);
      try{
        const u=typeof args[0]==='string'?args[0]:(args[0]?.url||'');
        const method=(args[1]?.method||(args[0] instanceof Request?args[0].method:'GET')).toUpperCase();
        if(method==='GET'&&u.includes('/api/fingerprints/person/')&&!u.includes('/preview')){
          const c=r.clone(),data=await c.json(),d=deletedKeys();
          if(Array.isArray(data)){
            const filtered=data.filter(x=>!d.has(`${x.fingerCode}|${x.position}|${x.sequenceNo}`));
            if(filtered.length!==data.length)return new Response(JSON.stringify(filtered),{status:r.status,statusText:r.statusText,headers:r.headers});
          }
        }
      }catch{}
      return r;
    };
  }
  async function pid(){const c=document.querySelector('.person-identity b')?.textContent?.trim()||'';if(!c)throw new Error('ไม่พบ Person ปัจจุบัน');const r=await fetch(`${API}/api/persons?search=${encodeURIComponent(c)}`,{headers:{Authorization:`Bearer ${token()}`}});if(!r.ok)throw new Error(`HTTP ${r.status}`);const a=await r.json();const p=Array.isArray(a)?a.find(x=>x.personCode===c):null;if(!p)throw new Error('ไม่พบ Person ปัจจุบัน');return p.id}
  function meta(item){const sub=item.closest('.gallery-subcolumn');const pl=sub?.querySelector('.gallery-subtitle')?.textContent?.trim()||'';const pos={ซ้าย:'left',กลาง:'center',ขวา:'right'}[pl];const seq=Number(((item.querySelector('.gallery-meta b')?.textContent||'').match(/\d+/)||['0'])[0]);const alt=item.querySelector('img')?.alt||'';const top=document.querySelector('.gallery-top strong')?.textContent||'';const finger=(alt.match(/\b(?:L|R)[1-5]\b/)||top.match(/\b(?:L|R)[1-5]\b/)||[''])[0];return{finger,pos,pl,seq}}
  function show(m,onConfirm){css();const modal=document.createElement('div');modal.className='fp-modal';modal.innerHTML=`<div class="fp-card"><div class="fp-head"><div class="fp-icon">🗑</div><div><div class="fp-title">ลบภาพลายนิ้วมือ?</div><div class="fp-text">ต้องการลบ ${m.finger} / ${m.pl} #${m.seq} หรือไม่<br>ภาพนี้จะถูกลบจากระบบและ Gallery</div></div></div><div class="fp-actions"><button class="fp-cancel">ยกเลิก</button><button class="fp-confirm">ยืนยันการลบ</button></div></div>`;document.body.appendChild(modal);const close=()=>modal.remove();modal.querySelector('.fp-cancel').onclick=close;modal.addEventListener('click',e=>{if(e.target===modal)close()});const b=modal.querySelector('.fp-confirm');b.onclick=async()=>{b.disabled=true;b.textContent='กำลังลบ...';try{await onConfirm();close()}catch(e){b.disabled=false;b.textContent='ยืนยันการลบ';alert(String(e))}}}
  function decrement(finger,pos){const row=[...document.querySelectorAll('.finger-table tbody tr')].find(r=>r.querySelector('.finger-select b')?.textContent?.trim()===finger);if(!row)return;const i={left:0,center:1,right:2}[pos];const cell=row.querySelectorAll('.finger-cell')[i];const span=cell?.querySelector('span');if(!span)return;const n=Math.max(0,Number(span.textContent||'0')-1);span.textContent=String(n);if(n===0)cell.classList.remove('has-images')}
  function bind(){css();patchFetch();document.querySelectorAll('.gallery-item').forEach(item=>{if(item.dataset.fpDel==='1')return;item.dataset.fpDel='1';item.style.position='relative';const b=document.createElement('button');b.type='button';b.className='fp-del';b.title='ลบภาพนี้';b.textContent='🗑';b.onclick=async e=>{e.preventDefault();e.stopPropagation();const m=meta(item);if(!m.finger||!m.pos||!m.seq)return;show(m,async()=>{const id=await pid();const r=await fetch(`${API}/api/fingerprints/person/${id}/${m.finger}/${m.pos}/${m.seq}`,{method:'DELETE',headers:{Authorization:`Bearer ${token()}`}});if(!r.ok){let d='';try{d=await r.text()}catch{}throw new Error(`HTTP ${r.status}${d?`: ${d}`:''}`)}localStorage.setItem(deletedKey(m.finger,m.pos,m.seq),'1');decrement(m.finger,m.pos);window.dispatchEvent(new CustomEvent('fingerprint-deleted',{detail:m}));item.classList.add('fp-removing');setTimeout(()=>item.remove(),380)})};item.appendChild(b)})}
  bind();new MutationObserver(()=>setTimeout(bind,0)).observe(document.body,{childList:true,subtree:true});
})();
