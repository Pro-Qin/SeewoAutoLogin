// import.js — 批量粘贴导入与 CSV 文件导入。依赖：core.js、ui.js。

// ===== 批量导入 =====
let batchTimer = null;
function setBatchBusy(busy) {
  const btn = document.getElementById('batchImportBtn');
  if (!btn) return;
  btn.disabled = !!busy;
  btn.textContent = busy ? '导入中…' : '批量导入';
}
function batchImport() {
  const ta = document.getElementById('batchImportText');
  const res = document.getElementById('batchImportResult');
  const text = ta ? ta.value : '';
  if (!text || !text.trim()) {
    if (res) res.innerHTML = '<div class="batch-line err">请先输入要导入的账号，每行一个</div>';
    showToast('请先输入要导入的账号', 'warn');
    return;
  }
  if (res) res.innerHTML = '';
  send({ type: 'batch-import', text: text });
  setBatchBusy(true);
  if (batchTimer) clearTimeout(batchTimer);
  batchTimer = setTimeout(function() {
    batchTimer = null;
    setBatchBusy(false);
    showToast('批量导入超时，请重试', 'warn');
  }, 30000);
}
function batchLineLevel(text) {
  return /失败|错误|无效|已存在|重复|格式|缺少|无法|跳过|fail|error|invalid|exist|skip/i.test(String(text || '')) ? 'err' : 'ok';
}
function renderBatchImportResult(msg) {
  msg = msg || {};
  if (batchTimer) { clearTimeout(batchTimer); batchTimer = null; }
  setBatchBusy(false);
  const res = document.getElementById('batchImportResult');
  const added = parseInt(msg.added, 10) || 0;
  const failed = parseInt(msg.failed, 10) || 0;
  if (res) {
    let html = '<div class="batch-line ' + (failed > 0 ? 'err' : 'ok') + '">导入完成：成功 ' + added + ' 个，失败 ' + failed + ' 个</div>';
    (msg.messages || []).forEach(function(m) {
      html += '<div class="batch-line ' + batchLineLevel(m) + '">' + esc(m) + '</div>';
    });
    res.innerHTML = html;
  }
  if (added > 0) showToast('批量导入完成：成功 ' + added + ' 个' + (failed > 0 ? '，失败 ' + failed + ' 个' : ''), failed > 0 ? 'warn' : 'ok');
  else if (failed > 0) showToast('批量导入失败 ' + failed + ' 个', 'error');
}


// ===== CSV 文件导入 =====
// 文件选择在 C# 侧弹系统对话框，用户挑文件的时间也算在等待里，所以超时给得比粘贴导入宽得多
let csvTimer = null;
const CSV_IMPORT_TIMEOUT = 120000;
const CSV_FAIL_LINES_SHOWN = 5;

function setCsvBusy(busy) {
  const btn = document.getElementById('importCsvBtn');
  if (!btn) return;
  btn.disabled = !!busy;
  btn.textContent = busy ? '等待选择文件…' : '选择 CSV 文件导入';
}

function importCsvFile() {
  const res = document.getElementById('batchImportResult');
  if (res) res.innerHTML = '';
  send({ type: 'import-csv' });
  setCsvBusy(true);
  showToast('请在弹窗里选择要导入的 CSV 文件', 'info');
  if (csvTimer) clearTimeout(csvTimer);
  csvTimer = setTimeout(function() {
    csvTimer = null;
    setCsvBusy(false);
    showToast('CSV 导入超时，请重试', 'warn');
  }, CSV_IMPORT_TIMEOUT);
}

function renderCsvImportResult(msg) {
  msg = msg || {};
  if (csvTimer) { clearTimeout(csvTimer); csvTimer = null; }
  setCsvBusy(false);

  if (msg.cancelled) { showToast('已取消 CSV 导入', 'info'); return; }

  const added = parseInt(msg.added, 10) || 0;
  const updated = parseInt(msg.updated, 10) || 0;
  const failed = parseInt(msg.failed, 10) || 0;
  const fails = (msg.messages || []).filter(function(m) { return !!m; });

  const res = document.getElementById('batchImportResult');
  if (res) {
    let html = '<div class="batch-line ' + (failed > 0 ? 'err' : 'ok') + '">CSV 导入完成：新增 '
      + added + ' 个，更新 ' + updated + ' 个，失败 ' + failed + ' 个</div>';
    // 明细只列前几条失败原因：整份失败清单对用户没用，还容易把区域撑得很长
    fails.slice(0, CSV_FAIL_LINES_SHOWN).forEach(function(m) {
      html += '<div class="batch-line err">' + esc(m) + '</div>';
    });
    if (fails.length > CSV_FAIL_LINES_SHOWN) {
      html += '<div class="batch-line err">…还有 ' + (fails.length - CSV_FAIL_LINES_SHOWN) + ' 条失败原因未显示</div>';
    }
    res.innerHTML = html;
  }

  if (added + updated > 0) {
    showToast('CSV 导入完成：新增 ' + added + ' 个，更新 ' + updated + ' 个'
      + (failed > 0 ? '，失败 ' + failed + ' 个' : ''), failed > 0 ? 'warn' : 'ok');
  } else if (failed > 0) {
    showToast('CSV 导入失败 ' + failed + ' 个，详见导入明细', 'error');
  } else {
    showToast('CSV 文件里没有可导入的账号', 'warn');
  }
}
