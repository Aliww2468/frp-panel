// Appearance is local to this installation's browser profile.
if (window.chrome?.webview) {
  document.querySelector('#check-update').classList.remove('hidden');
  document.querySelector('#check-update').addEventListener('click', () => window.chrome.webview.postMessage('update:open'));
}
const themeKey = 'frp-panel-theme';
const availableThemes = ['light','dark','sand','blue'];
function applyTheme(theme) {
  const selected = availableThemes.includes(theme) ? theme : 'light';
  document.documentElement.dataset.theme = selected;
  window.chrome?.webview?.postMessage(`theme:${selected}`);
  document.querySelectorAll('[data-theme-option]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.themeOption === selected)));
}
try { applyTheme(localStorage.getItem(themeKey)); } catch { applyTheme('light'); }
document.querySelector('#open-appearance').addEventListener('click', () => document.querySelector('#appearance-dialog').showModal());
document.querySelector('#close-appearance').addEventListener('click', () => document.querySelector('#appearance-dialog').close());
document.querySelectorAll('[data-theme-option]').forEach(button => button.addEventListener('click', () => {
  applyTheme(button.dataset.themeOption);
  const error = document.querySelector('#theme-save-error');
  error.textContent = '';
  try { localStorage.setItem(themeKey, button.dataset.themeOption); }
  catch { error.textContent = '当前风格已应用，但无法保存；重新打开后需再次选择。'; }
}));
window.addEventListener('storage', event => { if(event.key === themeKey || event.key === null) applyTheme(event.newValue); });

const $ = (selector) => document.querySelector(selector);
const escapeHTML = (value) => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icon = (name) => `<svg aria-hidden="true"><use href="#i-${name}"/></svg>`;
const state = {data:null, logs:[], filter:'all', page:'overview', loading:false, busy:false, original:null, editRevision:null, settingsDirty:false, settingsRevision:null};
let toastTimer;
function toast(message, error=false) { const el=$('#toast'); el.textContent=message; el.className=`toast${error?' error':''}`; clearTimeout(toastTimer); toastTimer=setTimeout(()=>el.classList.add('hidden'),6000); }
async function request(path, body) {
  const options = body === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json','X-Panel-Token':state.data?.token || ''},body:JSON.stringify(body)};
  const response=await fetch(path,{...options,signal:AbortSignal.timeout(20000)});
  const data=await response.json();
  if(!response.ok) throw new Error(data.error || '请求未完成');
  return data;
}
const running = (proxy) => proxy.status === 'running';
function statusBadge(proxy) {
  if(running(proxy)) return '<span class="badge"><i></i>运行中</span>';
  if(proxy.error) return `<span class="badge error" title="${escapeHTML(proxy.error)}"><i></i>连接异常</span>`;
  if(proxy.status==='offline') return '<span class="badge neutral"><i></i>未连接</span>';
  const labels={pending:'等待连接','new':'等待连接','start error':'启动失败','check failed':'检查失败','closed':'已关闭','wait start':'等待启动'};
  return `<span class="badge warning"><i></i>${escapeHTML(labels[proxy.status] || proxy.status || '等待连接')}</span>`;
}
function render() {
  const data=state.data;
  if(!data) return;
  const count=data.proxies.length, active=data.proxies.filter(running).length;
  $('#nav-count').textContent=count; $('#rule-count').textContent=count;
  $('#client-status').innerHTML=`${!data.configured?'待配置':data.connected?'运行中':data.processRunning?'连接中':'未连接'}<span class="status-dot ${data.connected?'':'neutral'}"></span>`;
  $('#client-description').textContent=data.configured&&!data.connected?(data.error||''):'';
  $('#total-count').innerHTML=`${count}<span>条规则</span>`;
  $('#protocol-count').textContent=`${data.proxies.filter(p=>p.type==='tcp').length} TCP · ${data.proxies.filter(p=>p.type==='udp').length} UDP`;
  $('#running-count').innerHTML=`${active}<span>条代理</span>`;
  $('#running-description').textContent=active?`${count-active} 条代理未运行`:'暂无活跃代理';
  $('#server-address').textContent=data.server.address || '尚未配置';
  $('#server-port').textContent=data.configured?`控制端口 ${data.server.port}`:'等待添加服务器';
  $('#topology-server').textContent=data.server.address || '等待添加服务器';
  $('#transport-label').textContent=data.server.tls?'TLS 加密通道':'未启用 TLS';
  $('#wire-label').textContent=active?'代理已建立':'等待连接';
  $('#path-status').className=`badge ${active?'':'neutral'}`;
  $('#path-status').innerHTML=`<i></i>${active?'代理运行中':data.connected?'等待代理上线':'等待客户端'}`;
  const canStop=data.connected||data.processRunning;
  $('#client-control').innerHTML=`${icon(!data.configured?'plus':canStop?'stop':'play')}<span>${!data.configured?'添加连接配置':canStop?'停止客户端':'启动客户端'}</span>`;
  $('#client-control').disabled=state.busy;
  $('#version').textContent=`v${data.version}`;
  $('#last-updated').textContent=`更新于 ${new Date(data.timestamp*1000).toLocaleTimeString('zh-CN',{hour12:false})}`;
  $('#setup-banner').classList.toggle('hidden',data.configured||state.page==='settings');
  $('#connection-edit-note').classList.toggle('hidden',!canStop);
  $('#save-connection').disabled=state.busy||canStop;
  $('#reset-connection').disabled=state.busy||canStop;
  $('#connection-form-title').textContent=data.configured?'编辑连接配置':'添加连接配置';
  $('#connection-config-status').innerHTML=`<i></i>${data.configured?'已配置':'待配置'}`;
  $('#connection-config-status').className=`badge ${data.configured?'':'neutral'}`;
  if(!state.settingsDirty && state.settingsRevision!==data.revision) loadConnectionForm();
  renderProxies();
}
function renderProxies() {
  if(!state.data)return;
  const query=$('#search').value.toLowerCase().trim();
  const rows=state.data.proxies.filter(p=>(state.filter==='all'||p.type===state.filter)&&[p.name,p.localIP,p.localPort,p.remotePort,state.data.server.address].some(value=>String(value).toLowerCase().includes(query)));
  $('#proxy-rows').innerHTML=rows.map(p=>{
    const index=state.data.proxies.indexOf(p);
    const publicAddress=`${state.data.server.address}:${p.remotePort}`;
    return `<tr><td><span class="proxy-name"><span class="proxy-icon ${p.type==='udp'?'udp':''}">${icon('link')}</span>${escapeHTML(p.name)}</span></td><td><span class="protocol ${p.type==='udp'?'udp':''}">${escapeHTML(p.type.toUpperCase())}</span></td><td class="mono">${escapeHTML(p.localIP||'127.0.0.1')}:${escapeHTML(p.localPort)}</td><td><div class="address-cell"><span class="mono">${escapeHTML(publicAddress)}</span><button class="icon-button" data-action="copy" data-index="${index}" title="复制公网地址" aria-label="复制 ${escapeHTML(p.name)} 公网地址">${icon('copy')}</button></div></td><td>${statusBadge(p)}</td><td><div class="row-actions"><button class="icon-button" data-action="edit" data-index="${index}" title="编辑代理" aria-label="编辑 ${escapeHTML(p.name)}" ${['tcp','udp'].includes(p.type)?'':'disabled'}>${icon('edit')}</button><button class="icon-button delete" data-action="delete" data-index="${index}" title="删除代理" aria-label="删除 ${escapeHTML(p.name)}">${icon('trash')}</button></div></td></tr>`;
  }).join('') || '<tr><td colspan="6"><div class="empty-state">'+(!state.data.configured?'请先添加服务器配置':query||state.filter!=='all'?'无匹配规则':'暂无代理规则')+'</div></td></tr>';
  $('#table-summary').textContent=`显示 ${rows.length} 条，共 ${state.data.proxies.length} 条代理规则`;
}
function renderLogs() {
  const query=$('#log-search').value.toLowerCase();
  let lines=state.logs.filter(line=>!query||line.toLowerCase().includes(query));
  if(state.page!=='logs') lines=lines.slice(-4);
  $('#log-lines').innerHTML=lines.map(raw=>{
    const line=raw.replace(/\x1b\[[0-9;]*m/g,'');
    const time=line.match(/\d{2}:\d{2}:\d{2}/)?.[0]||'—';
    const level=/\[(E|ERROR|F)\]|\berror\b/i.test(line)?'error':/\[(W|WARN)\]|\bwarn\b/i.test(line)?'warn':'info';
    const message=line.replace(/^.*?\d{2}:\d{2}:\d{2}(?:\.\d+)?\s*/,'').replace(/^\[[IWEF]\]\s*/, '');
    return `<div class="log-row"><span class="log-time">${time}</span><span class="log-level ${level}">${level.toUpperCase()}</span><span class="log-message">${escapeHTML(message)}</span></div>`;
  }).join('') || `<div class="empty-state">${query?'没有匹配的日志':'暂无日志'}</div>`;
}
const pages={overview:['概览','网络概览'],proxies:['代理规则','代理规则'],logs:['运行日志','运行日志'],settings:['连接配置','连接配置']};
function navigate() {
  const page=location.hash.slice(1);
  state.page=pages[page]?page:'overview';
  const [label,title]=pages[state.page];
  $('#breadcrumb').textContent=label; $('#page-title').innerHTML=`${title}<span class="title-dot">.</span>`;
  document.querySelectorAll('.nav-item').forEach(el=>{el.classList.toggle('active',el.dataset.page===state.page); if(el.dataset.page===state.page)el.setAttribute('aria-current','page');else el.removeAttribute('aria-current');});
  for(const section of ['overview','proxies','logs','settings']) $(`#${section}-section`).classList.toggle('hidden',section!==state.page&&!(state.page==='overview'&&['proxies','logs'].includes(section)));
  $('#log-toolbar').classList.toggle('hidden',state.page!=='logs'); $('#all-logs').classList.toggle('hidden',state.page==='logs');
  $('#logs-section').classList.toggle('logs-expanded',state.page==='logs');
  $('#setup-banner').classList.toggle('hidden',!state.data||state.data.configured||state.page==='settings');
  renderLogs();
}
async function refresh(manual=false) {
  if(state.loading || state.busy)return;
  state.loading=true;
  $('#refresh').disabled=true;
  try{
    const [data,logs]=await Promise.all([request('/api/state'),request('/api/logs')]);
    state.data=data;state.logs=logs.lines;
    $('#connection-error').classList.add('hidden');
    $('#add-proxy').disabled=false;
    render();renderLogs();
    if(manual)toast('状态已更新');
  }catch(error){
    $('#connection-error').textContent=`无法同步面板：${error.message}。当前显示的可能是上次数据。`;
    $('#connection-error').classList.remove('hidden');
    $('#last-updated').textContent='同步失败';
    $('#client-control').disabled=true; $('#add-proxy').disabled=true;
    if(manual)toast(error.message,true);
  }finally{state.loading=false;$('#refresh').disabled=false;}
}
function openProxy(proxy=null) {
  if(!state.data)return toast('请等待配置加载',true);
  if(!state.data.configured){location.hash='settings';toast('请先添加服务器配置');return;}
  $('#proxy-form').reset(); $('#form-error').textContent='';
  state.original=proxy?.name||null;state.editRevision=state.data.revision;
  $('#dialog-title').textContent=proxy?'编辑代理':'新建代理';
  $('#proxy-provider-note').classList.toggle('hidden',!!proxy);
  if(proxy)for(const key of ['name','type','localIP','localPort','remotePort'])$('#proxy-form').elements[key].value=proxy[key];
  $('#proxy-dialog').showModal();
}
let confirmedAction=null;
function confirmAction(title,description,label,callback) {
  $('#confirm-title').textContent=title;$('#confirm-description').textContent=description;$('#confirm-action').textContent=label;confirmedAction=callback;$('#confirm-dialog').showModal();
}
async function mutate(path,body={}) {
  if(state.busy)return;
  state.busy=true;$('#client-control').disabled=true;
  try{const result=await request(path,body);toast(result.message,!!result.warning);return true;}
  catch(error){toast(error.message,true);return false;}
  finally{state.busy=false;await refresh();}
}
$('#refresh').addEventListener('click',()=>refresh(true));
$('#search').addEventListener('input',renderProxies);
$('#log-search').addEventListener('input',renderLogs);
document.querySelectorAll('[data-filter]').forEach(button=>button.addEventListener('click',()=>{state.filter=button.dataset.filter;document.querySelectorAll('[data-filter]').forEach(el=>el.classList.toggle('selected',el===button));renderProxies();}));
$('#add-proxy').addEventListener('click',()=>openProxy());
document.querySelectorAll('.close-dialog').forEach(button=>button.addEventListener('click',()=>$('#proxy-dialog').close()));
document.querySelectorAll('.close-confirm').forEach(button=>button.addEventListener('click',()=>$('#confirm-dialog').close()));
$('#confirm-action').addEventListener('click',async()=>{const action=confirmedAction;$('#confirm-dialog').close();await action?.();});
$('#proxy-rows').addEventListener('click',async event=>{
  const button=event.target.closest('[data-action]');if(!button||state.busy)return;
  const proxy=state.data.proxies[Number(button.dataset.index)];
  if(button.dataset.action==='copy'){
    try{await navigator.clipboard.writeText(`${state.data.server.address}:${proxy.remotePort}`);toast('公网地址已复制');}catch{toast('浏览器未允许复制，请手动选择地址',true);}
  }
  if(button.dataset.action==='edit')openProxy(proxy);
  if(button.dataset.action==='delete'){
    const revision=state.data.revision;
    confirmAction('删除这条代理？',`将删除「${proxy.name}」。如果客户端正在运行，相关公网转发会在重载后断开。原配置会自动备份。`,'删除代理',()=>mutate('/api/proxy/delete',{original:proxy.name,revision}));
  }
});
$('#proxy-form').addEventListener('submit',async event=>{
  event.preventDefault();if(state.busy)return;
  const values=new FormData(event.target); const proxy=Object.fromEntries(values);
  proxy.localPort=Number(proxy.localPort);proxy.remotePort=Number(proxy.remotePort);
  state.busy=true;$('#save-proxy').disabled=true;$('#form-error').textContent='';
  try{const result=await request('/api/proxy/save',{proxy,original:state.original,revision:state.editRevision});$('#proxy-dialog').close();toast(result.message,!!result.warning);}
  catch(error){$('#form-error').textContent=error.message;}
  finally{state.busy=false;$('#save-proxy').disabled=false;await refresh();}
});
$('#client-control').addEventListener('click',()=>{
  if(!state.data)return;
  if(!state.data.configured){location.hash='settings';return;}
  if(state.data.connected||state.data.processRunning)confirmAction('停止客户端？','所有通过此客户端运行的公网转发将断开，本地服务本身不会停止。','停止客户端',()=>mutate('/api/control/stop'));
  else mutate('/api/control/start');
});
function updateTokenField() {
  const replace=$('#token-mode').value==='replace';
  $('#auth-token').disabled=!replace;
  $('#auth-token').required=replace;
  if(!replace)$('#auth-token').value='';
}
function loadConnectionForm() {
  if(!state.data)return;
  const form=$('#connection-form'), server=state.data.server;
  form.reset();
  for(const [key,value] of Object.entries({address:server.address,port:server.port,user:server.user||'',tls:String(server.tls),protocol:server.protocol,tokenMode:'keep'}))form.elements[key].value=value;
  $('#token-mode').options[0].textContent=server.hasToken?'保留现有 Token（已配置）':'暂不设置 Token';
  $('#connection-form-error').textContent='';
  state.settingsRevision=state.data.revision;state.settingsDirty=false;
  $('#connection-save-note').textContent='';
  updateTokenField();
}
$('#connection-form').addEventListener('input',()=>{state.settingsDirty=true;$('#connection-save-note').textContent='未保存';});
$('#connection-form').addEventListener('change',()=>{state.settingsDirty=true;$('#connection-save-note').textContent='未保存';updateTokenField();});
$('#discard-connection').addEventListener('click',loadConnectionForm);
$('#connection-form').addEventListener('submit',async event=>{
  event.preventDefault();if(state.busy||!state.data)return;
  const values=Object.fromEntries(new FormData(event.target));
  values.port=Number(values.port);values.tls=values.tls==='true';values.revision=state.settingsRevision;
  state.busy=true;$('#save-connection').disabled=true;$('#connection-form-error').textContent='';
  try{
    const result=await request('/api/connection/save',values);
    state.settingsDirty=false;state.settingsRevision=null;$('#auth-token').value='';toast(result.message);
  }catch(error){$('#connection-form-error').textContent=error.message;}
  finally{state.busy=false;await refresh();}
});
$('#reset-connection').addEventListener('click',()=>{
  if(!state.data||state.busy)return;
  const revision=state.data.revision;
  confirmAction('重置连接与代理？','服务器地址、用户标识、认证信息和全部代理规则将清空，日志从新记录开始。原配置和历史日志保留在本机，方便恢复。','重置配置',async()=>{
    if(await mutate('/api/connection/reset',{revision})){state.settingsDirty=false;state.settingsRevision=null;loadConnectionForm();location.hash='settings';}
  });
});
$('#download-logs').addEventListener('click',()=>{const url=URL.createObjectURL(new Blob([state.logs.join('\n')],{type:'text/plain;charset=utf-8'}));const link=document.createElement('a');link.href=url;link.download=`frpc-logs-${new Date().toISOString().slice(0,10)}.txt`;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
let pendingImport=null, importSequence=0, importing=false;
function clearImport(){pendingImport=null;importSequence++;$('#config-file').value='';$('#import-summary').classList.add('hidden');$('#import-error').textContent='';$('#apply-import').disabled=true;}
$('#import-config').addEventListener('click',()=>{if(!state.data)return toast('请等待配置加载',true);clearImport();$('#import-dialog').showModal();});
document.querySelectorAll('.close-import').forEach(button=>button.addEventListener('click',()=>{if(!importing)$('#import-dialog').close();}));
$('#import-dialog').addEventListener('cancel',event=>{if(importing)event.preventDefault();});
$('#import-dialog').addEventListener('close',clearImport);
$('#config-file').addEventListener('change',async event=>{
  const sequence=++importSequence, file=event.target.files[0];
  pendingImport=null;$('#apply-import').disabled=true;$('#import-summary').classList.add('hidden');$('#import-error').textContent='';
  if(!file)return;
  try{
    if(!file.name.toLowerCase().endsWith('.toml'))throw new Error('请选择 .toml 配置文件');
    if(file.size>256*1024)throw new Error('配置文件不能超过 256 KB');
    $('#import-error').textContent='正在读取并校验配置…';
    let content;
    try{content=new TextDecoder('utf-8',{fatal:true}).decode(await file.arrayBuffer());}catch{throw new Error('请将文件保存为 UTF-8 编码后重新选择');}
    const result=await request('/api/connection/import-preview',{filename:file.name,content});
    if(sequence!==importSequence)return;
    pendingImport={filename:file.name,content,revision:result.revision};
    $('#import-summary').innerHTML=`<b>配置校验通过</b><dl><dt>服务器</dt><dd>${escapeHTML(result.address)}:${escapeHTML(result.port)}</dd><dt>代理 / 访问者</dt><dd>${result.proxyCount} 条代理 · ${result.visitorCount} 个访问者</dd><dt>认证</dt><dd>${result.hasAuth?'已配置':'未配置'}</dd></dl>`;
    $('#import-summary').classList.remove('hidden');$('#import-error').textContent='';$('#apply-import').disabled=false;
  }catch(error){if(sequence===importSequence)$('#import-error').textContent=error.message;}
});
$('#apply-import').addEventListener('click',async()=>{
  if(!pendingImport||state.busy||importing)return;
  importing=true;state.busy=true;$('#apply-import').disabled=true;$('#config-file').disabled=true;
  $('#import-error').textContent='';
  try{const result=await request('/api/connection/import',pendingImport);state.settingsDirty=false;state.settingsRevision=null;$('#import-dialog').close();toast(result.message);}
  catch(error){$('#import-error').textContent=error.message;}
  finally{importing=false;state.busy=false;$('#config-file').disabled=false;$('#apply-import').disabled=!pendingImport;await refresh();}
});
window.addEventListener('hashchange',navigate);
navigate();refresh();setInterval(()=>{if(!document.hidden)refresh();},5000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
