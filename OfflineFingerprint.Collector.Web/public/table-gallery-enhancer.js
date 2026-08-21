(()=>{
  const STYLE_ID='table-gallery-enhancer-style';
  if(!document.getElementById(STYLE_ID)){
    const s=document.createElement('style');s.id=STYLE_ID;s.textContent=`
      .finger-row-new-save .finger-cell.has-images{animation:fp-cell-save .5s ease-out}
      @keyframes fp-cell-save{0%{transform:scale(.96);filter:brightness(1.2)}55%{transform:scale(1.04);filter:brightness(1.08)}100%{transform:scale(1);filter:none}}
      .gallery-item.fp-enter{animation:fp-gallery-in .5s cubic-bezier(.2,.8,.2,1)}
      @keyframes fp-gallery-in{0%{opacity:0;transform:translateY(-22px) scale(.97)}65%{opacity:1;transform:translateY(3px) scale(1.01)}100%{opacity:1;transform:translateY(0) scale(1)}}
      .collection-message{min-width:220px!important;max-width:360px!important;height:38px!important;font-size:14px!important}
      .collection-actions{align-items:center!important}
    `;document.head.appendChild(s)
  }
  function enhanceTable(){
    document.querySelectorAll('.finger-cell').forEach(btn=>{
      if(btn.dataset.fpEnhance==='1')return;btn.dataset.fpEnhance='1';
      btn.addEventListener('click',e=>{
        const live=!!document.querySelector('.scanner-toggle.on');
        if(!live){e.preventDefault();e.stopImmediatePropagation();}
      },{capture:true});
    });
  }
  function markGallery(){
    document.querySelectorAll('.gallery-scroll').forEach(scroll=>{
      const items=[...scroll.querySelectorAll('.gallery-item')];
      const sig=items.map(x=>x.getAttribute('data-fp-id')||x.textContent?.slice(0,80)||'').join('|');
      if(scroll.dataset.fpSig===sig)return;
      scroll.dataset.fpSig=sig;
      items.slice(0,3).forEach((item,i)=>{
        if(!item.classList.contains('fp-enter')){item.classList.add('fp-enter');if(i>0)item.style.animationDelay=`${i*45}ms`;}
      });
    });
  }
  function run(){enhanceTable();markGallery()}
  run();new MutationObserver(run).observe(document.body,{childList:true,subtree:true});
})();
