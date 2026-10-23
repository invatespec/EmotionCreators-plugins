import { data } from './data.js';
import { mountView } from '../../src/ui/view.js';
const findings = data.findings;
const view = mountView(data, 'report', { storage: false });
document.getElementById('report-mode').onchange = event => view.setReport(findings, event.target.value);
