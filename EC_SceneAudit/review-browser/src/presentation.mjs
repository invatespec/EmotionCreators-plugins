// 仅用于预览文字；关系分析使用结构化步骤，全文始终保留原文。
export function previewText(value) {
  return String(value ?? '').replace(/<script\b[\s\S]*?<\/script\s*>/giu, '').trim();
}
export function optionText(value) { return previewText(value).replace(/\s+/gu, ' '); }
