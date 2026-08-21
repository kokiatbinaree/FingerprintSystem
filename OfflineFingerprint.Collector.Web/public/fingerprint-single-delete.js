(() => {
  if (window.__fingerprintSingleDelete) return;
  window.__fingerprintSingleDelete = true;
  const API='http://localhost:5140';
  const DELETE_KEY='fp_deleted_targets_v2';
  const COUNT_KEY='fp_fingerprint_counts_v1';
  const token=()=>localStorage.getItem('of_token')||'';
  let modal=null;

  function readDeleted(){try{return JSON.parse(localStorage.getItem(DELETE_KEY)||'[]')}catch{return []}}
  function writeDeleted(list){localStorage.setItem(DELETE_KEY,JSON.stringify(list.slice(-500)))}
  function readCounts(){try{return JSON.parse(localStorage.getItem(COUNT_KEY)||'{}')}catch{return {}}}
  function writeCounts(value){localStorage.setItem(COUNT_KEY,JSON.stringify(value))}
  function targetKey(personId,finger,pos,seq){return `${personId}|${finger}|${pos}|${seq}`}
  function rememberDeleted(t){const list=readDeleted().filter(x=>x.key!==t.key);list.push(t);writeDeleted(list)}

  function filterFingerprintList(personId,items){
    if(!Array.isArray(items))return items;
    const deleted=readDeleted().filter(x=>x.personId===personId);
    return items.filter(item=>!deleted.some(d=>{
      if(targetKey(personId,item.fingerCode,item.position,item.sequenceNo)!==d.key)return false;
      const captured=Date.parse(item.capturedAtUtc||'');
      return !Number.isFinite(captured)||captured<=d.deletedAt;
    }));
  }

  function svgPlaceholder(){return new Response('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="480"><rect width="100%" height="100%" fill="white"/></svg>',{status:200,headers:{'Content-Type':'image/svg+xml','Cache-Control':'no-store'}})}

  const originalFetch=window.fetch.bind(window);
  window.fetch=async(...args)=>{
    const input=args[0];
    const url=typeof input==='string'?input:input?.url||'';
    const response=await originalFetch(...args);
    try{
      const listMatch=url.match(/\/api\/fingerprints\/person\/([0-9a-f-]{36})$/i);
      if(listMatch&&response.ok){
        const data=await response.clone().json();
        const filtered=filterFingerprintList(listMatch[1],data);
        if(filtered.length!==data.length){const headers=new Headers(response.headers);headers.set('Content-Type','application/json');return new Response(JSON.stringify(filtered),{status:200,headers})}
      }
      const previewMatch=url.match(/\/api\/fingerprints\/([0-9a-f-]{36})\/preview$/i);
      if(previewMatch&&readDeleted().some(x=>x.imageId===previewMatch[1]))return response.ok?response:svgPlaceholder();
    }catch{}
    return response;
  };

  function css(){
    if(document.getElementById('fpd-css'))return;
    const s=document.createElement('style');s.id='fpd-css';s.textContent=`
      .fp-del{position:absolute;right:7px;top:7px;z-index:5;width:28px;height:28px;border:1px solid #efb1aa;border-radius:7px;background:rgba(255,248,247,.96);color:#a2281b;font-size:14px;display:grid;place-items:center;opacity:0;transform:scale(.92);transition:.16s;cursor:pointer}
      .gallery-item:hover .fp-del,.gallery-item:focus-within .fp-del{opacity:1;transform:scale(1)}
      .gallery-item.fp-removing{pointer-events:none;animation:fpout .36s ease forwards}
      .gallery-item.fp-hidden-deleted{display:none!important}
      @keyframes fpout{0%{opacity:1;transform:translateY(0) scale(1);max-height:240px}100%{opacity:0;transform:translateY(-16px) scale(.92);max-height:0;margin:0;padding-top:0;padding-bottom:0;border-width:0}}
      .fp-modal{position:fixed;inset:0;z-index:9999;background:rgba(15,23,42,.46);display:grid;place-items:center;padding:20px}
      .fp-card{width:min(430px,92vw);background:#fff;border-radius:16px;box-shadow:0 24px 80px rgba(0,0,0,.28);overflow:hidden;animation:fpin .18s ease-out}
      @keyframes fpin{from{opacity:0;transform:translateY(8px) scale(.98)}to{opacity:1;transform:none}}
      .fp-head{padding:18px 20px 10px;display:flex;gap:12px}.fp-icon{width:42px;height:42px;border-radius:12px;display:grid;place-items:center;background:#fff0ed;color:#a2281b;font-size:20px}.fp-title{font-size:17px;font-weight:800;color:#172033}.fp-text{margin-top:4px;font-size:13px;color:#667085;line-height:1.5}.fp-actions{display:flex;justify-content:flex-end;gap:8px;padding:16px 20px 20px}.fp-cancel,.fp-confirm{border-radius:9px;padding:9px 14px;font:inherit;font-weight:700;cursor:pointer}.fp-cancel{border:1px solid #d0d7e2;background:#fff;color:#344054}.fp-confirm{border:1px solid #b42318;background:#b42318;color:#fff}.fp-confirm:disabled{opacity:.6;cursor:not-allowed}`;document.head.appendChild(s)
  }

  function close(){modal?.remove();modal=null}
  async function currentPerson(){
    const code=document.querySelector('.person-identity b')?.textContent?.trim()||'';
    if(!code)throw new Error('ไม่พบ Person ปัจจุบัน');
    const r=await originalFetch(`${API}/api/persons?search=${encodeURIComponent(code)}`,{headers:{Authorization:`Bearer ${token()}`}});
    if(!r.ok)throw new Error(`HTTP ${r.status}`);
    const a=await r.json();const p=Array.isArray(a)?a.find(x=>x.personCode===code):null;
    if(!p)throw new Error('ไม่พบ Person ปัจจุบัน');return p;
  }
  async function currentFingerprintRows(personId){
    const r=await originalFetch(`${API}/api/fingerprints/person/${personId}`,{headers:{Authorization:`Bearer ${token()}`,Accept:'application/json'}});
    if(!r.ok)throw new Error(`HTTP ${r.status}`);return await r.json();
  }
  function countsFromRows(rows){const out={};for(const x of rows||[]){const k=`${x.fingerCode}|${x.position}`;out[k]=(out[k]||0)+1}return out}

  function syncTableFromCache(){
    const counts=readCounts();
    document.querySelectorAll('.finger-table tbody tr').forEach(row=>{
      const finger=(row.querySelector('.finger-select')?.textContent||'').match(/\b(?:L|R)[1-5]\b/)?.[0];
      if(!finger)return;
      const cells=[...row.querySelectorAll('.finger-cell')];
      for(let i=0;i<cells.length;i++){
        const pos=['left','center','right'][i];
        const key=`${finger}|${pos}`;
        if(counts[key]===undefined)continue;
        const value=Number(counts[key])||0;
        const span=cells[i].querySelector('span');if(span)span.textContent=String(value);
        cells[i].classList.toggle('has-images',value>0);
      }
    });
  }

  function hideDeletedGalleryItems(){
    const deleted=readDeleted();if(!deleted.length)return;
    const code=document.querySelector('.person-identity b')?.textContent?.trim()||'';
    document.querySelectorAll('.gallery-item').forEach(item=>{
      const sub=item.closest('.gallery-subcolumn');const pl=sub?.querySelector('.gallery-subtitle')?.textContent?.trim()||'';const pos={ซ้าย:'left',กลาง:'center',ขวา:'right'}[pl];
      const seq=Number(((item.querySelector('.gallery-meta b')?.textContent||'').match(/\d+/)||['0'])[0]);
      const alt=item.querySelector('img')?.alt||'';const top=document.querySelector('.gallery-top strong')?.textContent||'';const finger=(alt.match(/\b(?:L|R)[1-5]\b/)||top.match(/\b(?:L|R)[1-5]\b/)||[''])[0];
      if(deleted.some(d=>d.fingerCode===finger&&d.position===pos&&d.sequenceNo===seq&&(!code||d.personCode===code)))item.classList.add('fp-hidden-deleted');
    });
  }

  function show(m,onConfirm){
    css();modal=document.createElement('div');modal.className='fp-modal';
    modal.innerHTML=`<div class="fp-card"><div class="fp-head"><div class="fp-icon">🗑</div><div><div class="fp-title">ลบภาพลายนิ้วมือ?</div><div class="fp-text">ต้องการลบ ${m.finger} / ${m.pl} #${m.seq} หรือไม่<br>ภาพจะถูกลบออกจาก Gallery หลังยืนยัน</div></div></div><div class="fp-actions"><button class="fp-cancel">ยกเลิก</button><button class="fp-confirm">ยืนยันการลบ</button></div></div>`;
    document.body.appendChild(modal);modal.querySelector('.fp-cancel').onclick=close;
    const b=modal.querySelector('.fp-confirm');b.onclick=async()=>{b.disabled=true;b.textContent='กำลังลบ...';try{await onConfirm();close()}catch(e){b.disabled=false;b.textContent='ยืนยันการลบ';alert(String(e))}};
    modal.addEventListener('click',e=>{if(e.target===modal)close()});
  }

  function meta(item){const sub=item.closest('.gallery-subcolumn');const pl=sub?.querySelector('.gallery-subtitle')?.textContent?.trim()||'';const pos={ซ้าย:'left',กลาง:'center',ขวา:'right'}[pl];const seq=Number(((item.querySelector('.gallery-meta b')?.textContent||'').match(/\d+/)||['0'])[0]);const alt=item.querySelector('img')?.alt||'';const top=document.querySelector('.gallery-top strong')?.textContent||'';const finger=(alt.match(/\b(?:L|R)[1-5]\b/)||top.match(/\b(?:L|R)[1-5]\b/)||[''])[0];return{finger,pos,pl,seq}}

  function bind(){
    css();hideDeletedGalleryItems();syncTableFromCache();
    document.querySelectorAll('.gallery-item').forEach(item=>{
      if(item.dataset.fpDel==='1')return;
      item.dataset.fpDel='1';item.style.position='relative';
      const b=document.createElement('button');b.type='button';b.className='fp-del';b.title='ลบภาพนี้';b.textContent='🗑';
      b.onclick=async e=>{
        e.preventDefault();e.stopPropagation();const m=meta(item);if(!m.finger||!m.pos||!m.seq)return;
        show(m,async()=>{
          const person=await currentPerson();const rows=await currentFingerprintRows(person.id);const target=rows.find(x=>x.fingerCode===m.finger&&x.position===m.pos&&x.sequenceNo===m.seq);if(!target)throw new Error('ไม่พบภาพลายนิ้วมือที่ต้องการลบ');
          const r=await originalFetch(`${API}/api/fingerprints/person/${person.id}/${m.finger}/${m.pos}/${m.seq}`,{method:'DELETE',headers:{Authorization:`Bearer ${token()}`}});
          if(!r.ok){let d='';try{d=await r.text()}catch{}throw new Error(`HTTP ${r.status}${d?`: ${d}`:''}`)}
          rememberDeleted({key:targetKey(person.id,m.finger,m.pos,m.seq),imageId:target.id,personId:person.id,personCode:person.personCode,fingerCode:m.finger,position:m.pos,sequenceNo:m.seq,deletedAt:Date.now()});
          const actual=rows.filter(x=>x.id!==target.id);const actualCounts=countsFromRows(actual);const counts=readCounts();
          for(const k of Object.keys(counts))if(k.includes('|'))delete counts[k];
          for(const[k,v]of Object.entries(actualCounts))counts[k]=v;
          writeCounts(counts);
          item.classList.add('fp-removing');setTimeout(()=>item.remove(),360);syncTableFromCache();
        });
      };
      item.appendChild(b)
    })
  }

  bind();new MutationObserver(()=>setTimeout(bind,0)).observe(document.body,{childList:true,subtree:true});
})();
