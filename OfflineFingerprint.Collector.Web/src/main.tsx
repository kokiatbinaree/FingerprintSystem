import React,{useEffect,useMemo,useRef,useState}from'react';
import{createRoot}from'react-dom/client';
import'./styles.css';

const API='http://localhost:5140';
const AGENT='http://127.0.0.1:15271';
const AGENT_WS='ws://127.0.0.1:15271/ws/preview';
const IDLE_MS=2*60*1000;

type User={id:string;username:string;displayName:string;role:string};
type Person={id:string;personCode:string;firstName:string;lastName:string;nationalId?:string|null;note?:string|null};
type Finger={id:string;fingerCode:string;position:string;sequenceNo:number;width:number;height:number;capturedAtUtc:string;syncStatus:string};
type GalleryItem=Finger&{url:string};
type LiveState={width:number;height:number;grayBase64:string;running:boolean};

function ZoomBox({children,resetKey}:{children:React.ReactNode;resetKey?:string|number}){
  const[scale,setScale]=useState(1),[pan,setPan]=useState({x:0,y:0}),ref=useRef<HTMLDivElement|null>(null);
  useEffect(()=>{setScale(1);setPan({x:0,y:0})},[resetKey]);
  function onWheel(e:React.WheelEvent){e.preventDefault();const el=ref.current;if(!el)return;const r=el.getBoundingClientRect(),cx=r.width/2,cy=r.height/2,px=e.clientX-r.left,py=e.clientY-r.top;const next=Math.min(5,Math.max(1,scale*(e.deltaY<0?1.18:1/1.18)));if(next===scale)return;setPan({x:(px-cx)-((px-cx-pan.x)*(next/scale)),y:(py-cy)-((py-cy-pan.y)*(next/scale))});setScale(next)}
  return <div ref={ref} className="zoom-box" onWheel={onWheel} onDoubleClick={()=>{setScale(1);setPan({x:0,y:0})}}><div className="zoom-layer" style={{transform:`translate(calc(-50% + ${pan.x}px),calc(-50% + ${pan.y}px)) scale(${scale})`}}>{children}</div><div className="zoom-badge">{Math.round(scale*100)}%</div></div>
}

const fingers=['L1','L2','L3','L4','L5','R1','R2','R3','R4','R5'];
const fingerLabels:Record<string,string>={L1:'นิ้วโป้ง',L2:'นิ้วชี้',L3:'นิ้วกลาง',L4:'นิ้วนาง',L5:'นิ้วก้อย',R1:'นิ้วโป้ง',R2:'นิ้วชี้',R3:'นิ้วกลาง',R4:'นิ้วนาง',R5:'นิ้วก้อย'};
const positions=[['left','ซ้าย'],['center','กลาง'],['right','ขวา']] as const;
const emptyPerson={personCode:'',firstName:'',lastName:'',nationalId:'',note:''};

async function api(path:string,init:RequestInit={},token?:string){const h=new Headers(init.headers);h.set('Content-Type','application/json');if(token)h.set('Authorization',`Bearer ${token}`);const r=await fetch(API+path,{...init,headers:h});if(!r.ok){let d='';try{d=await r.text()}catch{}throw new Error(d?`HTTP ${r.status}: ${d}`:`HTTP ${r.status}`)}return r.status===204?null:r.json()}
async function agent(path:string,init:RequestInit={}){const r=await fetch(AGENT+path,{...init,headers:{...(init.headers||{}),'Content-Type':'application/json'}});if(!r.ok){let d='';try{d=await r.text()}catch{}throw new Error(d?`Agent HTTP ${r.status}: ${d}`:`Agent HTTP ${r.status}`)}return r.status===204?null:r.json()}

function Login({onLogin}:{onLogin:(token:string,user:User)=>void}){const[u,setU]=useState('admin'),[p,setP]=useState('ChangeMe123!'),[busy,setBusy]=useState(false),[err,setErr]=useState('');async function submit(e:React.FormEvent){e.preventDefault();setBusy(true);setErr('');try{const v=await api('/api/auth/login',{method:'POST',body:JSON.stringify({username:u,password:p})});onLogin(v.token,v.user)}catch(x){setErr(x instanceof Error?x.message:'ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง')}finally{setBusy(false)}}return <div className="login"><form className="card login-card" onSubmit={submit}><div className="brand-mark">FP</div><h1>Fingerprint Collection</h1><p>ระบบเก็บลายนิ้วมือ Offline</p><label>ชื่อผู้ใช้<input value={u} onChange={e=>setU(e.target.value)}/></label><label>รหัสผ่าน<input type="password" value={p} onChange={e=>setP(e.target.value)}/></label>{err&&<div className="error">{err}</div>}<button disabled={busy}>{busy?'กำลังเข้าสู่ระบบ...':'เข้าสู่ระบบ'}</button></form></div>}

function PersonEditor({initial,onClose,onSaved,token}:{initial:Person|null;onClose:()=>void;onSaved:(p:Person)=>void;token:string}){
  const[form,setForm]=useState(initial?{personCode:initial.personCode,firstName:initial.firstName,lastName:initial.lastName,nationalId:initial.nationalId||'',note:initial.note||''}:emptyPerson),[busy,setBusy]=useState(false),[err,setErr]=useState('');
  async function save(e:React.FormEvent){e.preventDefault();setBusy(true);setErr('');try{const p=initial?await api(`/api/persons/${initial.id}`,{method:'PUT',body:JSON.stringify({...initial,...form})},token):await api('/api/persons',{method:'POST',body:JSON.stringify(form)},token);onSaved(p);onClose()}catch(x){setErr(String(x))}finally{setBusy(false)}}
  return <div className="modal"><div className="dialog person-dialog"><div className="dialog-head"><div><h2>{initial?'แก้ไขลูกค้า':'เพิ่มลูกค้า'}</h2><span>ข้อมูล Person</span></div><button className="icon-button" onClick={onClose}>×</button></div><form className="dialog-body form" onSubmit={save}><div className="form-grid"><label>รหัสลูกค้า<input required value={form.personCode} onChange={e=>setForm({...form,personCode:e.target.value})}/></label><label>ชื่อ<input required value={form.firstName} onChange={e=>setForm({...form,firstName:e.target.value})}/></label><label>นามสกุล<input value={form.lastName} onChange={e=>setForm({...form,lastName:e.target.value})}/></label><label>เลขบัตรประชาชน<input value={form.nationalId} onChange={e=>setForm({...form,nationalId:e.target.value})}/></label><label className="span-2">หมายเหตุ<textarea value={form.note} onChange={e=>setForm({...form,note:e.target.value})}/></label></div>{err&&<div className="error">{err}</div>}<div className="dialog-actions"><button type="button" className="secondary" onClick={onClose}>ยกเลิก</button><button type="submit" className="primary" disabled={busy}>{busy?'กำลังบันทึก...':'บันทึกข้อมูล'}</button></div></form></div></div>
}

function App(){
  const[token,setToken]=useState(localStorage.getItem('of_token')||''),[user,setUser]=useState<User|null>(()=>{try{return JSON.parse(localStorage.getItem('of_user')||'null')}catch{return null}});
  const[persons,setPersons]=useState<Person[]>([]),[search,setSearch]=useState(''),[view,setView]=useState<'persons'|'fingerprints'>('persons'),[selectedPerson,setSelectedPerson]=useState<Person|null>(null),[editing,setEditing]=useState<Person|null|false>(false);
  const[finger,setFinger]=useState('L1'),[position,setPosition]=useState<'left'|'center'|'right'>('center'),[images,setImages]=useState<Finger[]>([]),[gallery,setGallery]=useState<GalleryItem[]>([]),[galleryLoading,setGalleryLoading]=useState(false),[selectedImage,setSelectedImage]=useState<GalleryItem|null>(null);
  const[live,setLive]=useState<LiveState|null>(null),[scannerBusy,setScannerBusy]=useState(false),[notice,setNotice]=useState(''),[err,setErr]=useState(''),[scannerError,setScannerError]=useState('');
  const wsRef=useRef<WebSocket|null>(null),canvasRef=useRef<HTMLCanvasElement|null>(null),latestGrayRef=useRef(''),idleTimerRef=useRef<number|undefined>(undefined);

  function login(t:string,u:User){localStorage.setItem('of_token',t);localStorage.setItem('of_user',JSON.stringify(u));setToken(t);setUser(u)}
  function positionLabel(p:string){return positions.find(x=>x[0]===p)?.[1]||p}
  function personName(p:Person){return `${p.firstName} ${p.lastName}`.trim()}
  function touchActivity(){if(!live)return;window.clearTimeout(idleTimerRef.current);idleTimerRef.current=window.setTimeout(()=>{closeScanner('ปิดเครื่องสแกนอัตโนมัติ เนื่องจากไม่มีการทำงาน 2 นาที')},IDLE_MS)}

  async function closeScanner(message?:string){window.clearTimeout(idleTimerRef.current);idleTimerRef.current=undefined;const ws=wsRef.current;if(ws){try{ws.close()}catch{}wsRef.current=null}try{await agent('/device/close',{method:'POST'})}catch{}latestGrayRef.current='';setScannerBusy(false);setLive(null);setSelectedImage(null);if(message){setNotice(message);setErr('')}}
  async function openScanner(){if(!selectedPerson){setErr('กรุณาเลือก Person ก่อน');return}if(live)return;setErr('');setNotice('กำลังเปิดเครื่องสแกน...');setScannerError('');try{const r=await agent('/device/open',{method:'POST'});if(!r?.ok)throw new Error('เปิด Futronic FS80H ไม่สำเร็จ');const ws=new WebSocket(AGENT_WS);ws.binaryType='blob';wsRef.current=ws;latestGrayRef.current='';setSelectedImage(null);setLive({width:320,height:480,grayBase64:'',running:true});touchActivity();ws.onmessage=e=>{if(typeof e.data==='string')return;drawBlob(e.data as Blob)};ws.onerror=()=>{setScannerError('เชื่อมต่อ FutronicBridge ไม่สำเร็จ');closeScanner('เครื่องสแกนปิดแล้วเนื่องจากการเชื่อมต่อล้มเหลว')};ws.onclose=()=>{wsRef.current=null};setNotice('เปิดเครื่องสแกนแล้ว')}catch(x){setScannerError(String(x));setErr(String(x))}}
  async function toggleScanner(){touchActivity();if(live)await closeScanner('ปิดเครื่องสแกนแล้ว');else await openScanner()}

  async function loadPersons(){try{setPersons(await api('/api/persons'+(search?`?search=${encodeURIComponent(search)}`:''),{},token))}catch(x){setErr(String(x))}}
  useEffect(()=>{if(token)loadPersons()},[token]);
  async function loadFingerprints(personId:string){try{setImages(await api(`/api/fingerprints/person/${personId}`,{},token))}catch(x){setErr(String(x))}}
  useEffect(()=>{if(selectedPerson)loadFingerprints(selectedPerson.id)},[selectedPerson]);

  const cellCounts=useMemo(()=>{const m:Record<string,number>={};for(const x of images)m[`${x.fingerCode}|${x.position}`]=(m[`${x.fingerCode}|${x.position}`]||0)+1;return m},[images]);
  const selectedFingerImages=useMemo(()=>images.filter(x=>x.fingerCode===finger).sort((a,b)=>Date.parse(b.capturedAtUtc)-Date.parse(a.capturedAtUtc)||b.sequenceNo-a.sequenceNo),[images,finger]);
  const galleryByPosition=useMemo(()=>({left:selectedFingerImages.filter(x=>x.position==='left'),center:selectedFingerImages.filter(x=>x.position==='center'),right:selectedFingerImages.filter(x=>x.position==='right')}),[selectedFingerImages]);

  useEffect(()=>{let cancelled=false;gallery.forEach(x=>URL.revokeObjectURL(x.url));setGallery([]);if(!selectedPerson||!finger)return;(async()=>{setGalleryLoading(true);try{const items=await Promise.all(selectedFingerImages.map(async x=>{const r=await fetch(`${API}/api/fingerprints/${x.id}/preview`,{headers:{Authorization:`Bearer ${token}`}});if(!r.ok)throw new Error(`HTTP ${r.status}`);return{...x,url:URL.createObjectURL(await r.blob())}}));if(!cancelled)setGallery(items)}catch(x){if(!cancelled)setErr(`โหลดภาพลายนิ้วมือไม่สำเร็จ: ${String(x)}`)}finally{if(!cancelled)setGalleryLoading(false)}})();return()=>{cancelled=true}},[selectedPerson,finger,images,token]);

  function selectFinger(f:string){setFinger(f);setSelectedImage(null);setErr('');touchActivity()}
  async function selectCell(f:string,p:'left'|'center'|'right'){touchActivity();setErr('');
    if(!live){setFinger(f);setPosition(p);setSelectedImage(null);return}
    if(f!==finger){setFinger(f);setPosition(p);setSelectedImage(null);setNotice(`เลือก ${f} / ${positionLabel(p)} แล้ว`);return}
    await saveLatest(p);
  }
  function encodeBase64(bytes:Uint8Array){let s='';for(let i=0;i<bytes.length;i+=0x8000)s+=String.fromCharCode(...bytes.slice(i,i+0x8000));return btoa(s)}
  function drawBlob(blob:Blob){const url=URL.createObjectURL(blob),c=canvasRef.current;if(!c){URL.revokeObjectURL(url);return}const img=new Image();img.onload=()=>{try{c.width=320;c.height=480;const ctx=c.getContext('2d');if(!ctx)return;ctx.imageSmoothingEnabled=false;ctx.clearRect(0,0,320,480);ctx.drawImage(img,0,0,320,480);const d=ctx.getImageData(0,0,320,480),gray=new Uint8Array(320*480);for(let p=0,j=0;j<gray.length;j++,p+=4){const inv=255-d.data[p];gray[j]=inv;d.data[p]=inv;d.data[p+1]=inv;d.data[p+2]=inv;d.data[p+3]=255}ctx.putImageData(d,0,0);latestGrayRef.current=encodeBase64(gray);setLive(v=>v?{...v,grayBase64:latestGrayRef.current}:v)}finally{URL.revokeObjectURL(url)}};img.onerror=()=>URL.revokeObjectURL(url);img.src=url}
  async function saveLatest(targetPosition:'left'|'center'|'right'){if(!selectedPerson||!live)return false;const grayBase64=latestGrayRef.current||live.grayBase64;if(!grayBase64){setErr('ยังไม่ได้รับภาพจาก Futronic FS80H');return false}setPosition(targetPosition);setScannerBusy(true);setErr('');setNotice(`กำลังบันทึก ${finger} / ${positionLabel(targetPosition)}...`);touchActivity();try{await api('/api/capture/confirm',{method:'POST',body:JSON.stringify({personId:selectedPerson.id,fingerCode:finger,position:targetPosition,width:live.width,height:live.height,grayBase64})},token);await loadFingerprints(selectedPerson.id);setNotice(`บันทึก ${finger} / ${positionLabel(targetPosition)} สำเร็จ`);return true}catch(x){setErr(String(x));return false}finally{setScannerBusy(false);touchActivity()}}
  function showStored(item:GalleryItem){setSelectedImage(item);setErr('');touchActivity()}

  function goFingerprints(p:Person){setSelectedPerson(p);setView('fingerprints');setFinger('L1');setPosition('center');setSelectedImage(null);setNotice('');setErr('')}
  function onPersonSaved(p:Person){setPersons(v=>{const i=v.findIndex(x=>x.id===p.id);if(i<0)return[p,...v];const a=[...v];a[i]=p;return a});if(selectedPerson?.id===p.id)setSelectedPerson(p);setNotice('บันทึกข้อมูลลูกค้าแล้ว')}
  async function deletePerson(p:Person){if(!confirm(`ต้องการลบ ${p.personCode} ${p.firstName} ${p.lastName} หรือไม่?`))return;try{await api(`/api/persons/${p.id}`,{method:'DELETE'},token);setPersons(v=>v.filter(x=>x.id!==p.id));if(selectedPerson?.id===p.id){await closeScanner();setSelectedPerson(null);setView('persons')}setNotice('ลบข้อมูลแล้ว')}catch(x){setErr(String(x))}}
  function logout(){closeScanner();localStorage.removeItem('of_token');localStorage.removeItem('of_user');setToken('');setUser(null)}

  useEffect(()=>()=>{window.clearTimeout(idleTimerRef.current);const ws=wsRef.current;if(ws){try{ws.close()}catch{}}},[]);
  const topMessage=err||notice;

  if(!token||!user)return <Login onLogin={login}/>;
  return <div className="app-shell">
    <header className="app-header"><div className="header-title"><div className="header-main">FINGERPRINT COLLECTION</div><div className="header-sub">ระบบเก็บลายนิ้วมือ Offline</div></div><div className="header-user"><span>{user.displayName}</span><button className="ghost" onClick={logout}>ออกจากระบบ</button></div></header>
    {view==='persons'?<main className="page persons-page"><div className="page-top"><div><h1>จัดการลูกค้า</h1><p>ค้นหา แก้ไข และเข้าสู่ส่วนเก็บลายนิ้วมือ</p></div><button className="primary big" onClick={()=>setEditing(null)}>＋ เพิ่มลูกค้า</button></div><section className="card customer-card"><div className="search-bar"><input placeholder="ค้นหา รหัสลูกค้า / ชื่อ / นามสกุล" value={search} onChange={e=>setSearch(e.target.value)} onKeyDown={e=>e.key==='Enter'&&loadPersons()}/><button className="primary" onClick={loadPersons}>ค้นหา</button></div><div className="table-wrap"><table className="customer-table"><thead><tr><th>รหัส</th><th>ชื่อ</th><th>นามสกุล</th><th>เลขบัตร</th><th>หมายเหตุ</th><th className="actions-col">จัดการ</th></tr></thead><tbody>{persons.map(p=><tr key={p.id}><td><b>{p.personCode}</b></td><td>{p.firstName}</td><td>{p.lastName}</td><td>{p.nationalId||'-'}</td><td>{p.note||'-'}</td><td><div className="row-actions"><button className="link-button" onClick={()=>goFingerprints(p)}>เก็บลายนิ้วมือ</button><button className="icon-action" onClick={()=>setEditing(p)}>แก้ไข</button><button className="icon-action danger-text" onClick={()=>deletePerson(p)}>ลบ</button></div></td></tr>)}{persons.length===0&&<tr><td colSpan={6} className="empty-cell">ไม่พบข้อมูลลูกค้า</td></tr>}</tbody></table></div></section></main>:<main className="fingerprint-page">
      <div className="collection-top"><div className="person-heading"><button className="back-button" onClick={()=>{closeScanner();setView('persons')}}>← ลูกค้า</button><div><div className="collection-title">เก็บลายนิ้วมือ</div><div className="person-identity"><b>{selectedPerson?.personCode}</b><span>{selectedPerson?personName(selectedPerson):''}</span>{selectedPerson?.nationalId&&<span>เลขบัตร {selectedPerson.nationalId}</span>}</div></div></div><div className="collection-actions">{topMessage&&<span className={`collection-message ${err?'error':'success'}`}>{topMessage}</span>}<button className="secondary" onClick={()=>selectedPerson&&setEditing(selectedPerson)}>แก้ไขข้อมูลลูกค้า</button></div></div>
      <section className="capture-workspace">
        <div className="capture-column preview-column"><div className="preview-shell">{selectedImage?<><ZoomBox resetKey={selectedImage.id}><img src={selectedImage.url} alt="fingerprint"/></ZoomBox><button className="back-live" onClick={()=>{setSelectedImage(null);touchActivity()}}>กลับ Realtime</button></>:live?<ZoomBox resetKey="live"><canvas ref={canvasRef} width={320} height={480}/>{!live.grayBase64&&<div className="preview-empty">กำลังรอภาพจาก Futronic FS80H...</div>}</ZoomBox>:<div className="preview-empty large">เลือกภาพจาก Gallery</div>}</div><div className="preview-caption">{selectedImage?`${selectedImage.fingerCode} / ${positionLabel(selectedImage.position)} / #${selectedImage.sequenceNo}`:live?'Realtime • Invert • 320 × 480':'ยังไม่ได้เลือกภาพ'}</div></div>
        <div className="capture-column gallery-column"><div className="gallery-top"><div><strong>{finger} · {fingerLabels[finger]}</strong><span>{selectedFingerImages.length} ภาพของนิ้วนี้</span></div><div className="gallery-actions"><button className={`scanner-toggle ${live?'on':'off'}`} disabled={scannerBusy} onClick={toggleScanner}>{live?'ปิดเครื่องสแกน':'เปิดเครื่องสแกน'}</button></div></div><div className="gallery-columns">{positions.map(([p,label])=><div className="gallery-subcolumn" key={p}><div className="gallery-subtitle">{label}</div><div className="gallery-scroll">{galleryLoading&&<div className="gallery-loading">กำลังโหลด...</div>}{!galleryLoading&&galleryByPosition[p as 'left'|'center'|'right'].length===0&&<div className="gallery-empty">ไม่มีรูป</div>}{galleryByPosition[p as 'left'|'center'|'right'].map(g=><button key={g.id} className={selectedImage?.id===g.id?'gallery-item selected':'gallery-item'} onClick={()=>showStored(g)}><img src={g.url} alt={`${g.fingerCode} ${label}`}/><div className="gallery-meta"><b>#{g.sequenceNo}</b><small>{new Date(g.capturedAtUtc).toLocaleString('th-TH')}</small></div></button>)}</div></div>)}</div><div className="gallery-help">{live?'เลือกแถวนิ้วที่ต้องการ แล้วกดช่อง ซ้าย / กลาง / ขวา เพื่อบันทึกต่อ':'เปิดเครื่องสแกนเพื่อเริ่ม Realtime'}</div></div>
        <div className="capture-column table-column"><div className="table-top"><strong>นิ้ว</strong><div className="table-target">{live?`กำลัง Realtime • ${finger} • เลือกตำแหน่งเพื่อบันทึก`:`เลือก ${finger} / ${positionLabel(position)}`}</div></div><div className="finger-table-wrap"><table className="finger-table"><thead><tr><th>นิ้ว</th>{positions.map(([v,l])=><th key={v}>{l}</th>)}</tr></thead><tbody>{fingers.map(f=>{const activeFinger=f===finger;return <tr key={f} className={activeFinger?'finger-row-active':''}><td className="finger-label"><button className="finger-select" onClick={()=>selectFinger(f)} disabled={scannerBusy} title="เลือกนิ้ว"> <b>{f}</b><span>{fingerLabels[f]}</span></button></td>{positions.map(([p])=>{const n=cellCounts[`${f}|${p}`]||0,active=activeFinger&&position===p;return <td key={p}><button className={`finger-cell ${active?'active':''} ${n>0?'has-images':''}`} onClick={()=>selectCell(f,p as any)} disabled={scannerBusy} title={live?'บันทึกภาพล่าสุดลงตำแหน่งนี้':'เลือกนิ้วและตำแหน่ง'}><span>{n}</span></button></td>})}</tr>})}</tbody></table></div><div className="table-legend"><span><i className="legend-dot selected-dot"/> ช่องที่เลือก</span><span><i className="legend-dot saved-dot"/> มีภาพแล้ว</span></div></div>
      </section>
    </main>}
    {editing!==false&&<PersonEditor initial={editing||null} onClose={()=>setEditing(false)} onSaved={onPersonSaved} token={token}/>} 
  </div>
}

createRoot(document.getElementById('root')!).render(<App/>);
