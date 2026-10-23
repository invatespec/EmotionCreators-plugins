import fs from 'node:fs/promises';
import { parseArgs } from 'node:util';
import { loadBundle } from './src/bundle.mjs';
import { directoryReader } from './src/node-files.mjs';
import { validateReport } from './src/report.mjs';

try {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: { bundle: { type: 'string' }, report: { type: 'string' }, previous: { type: 'string' } },
  });
  if (positionals.length !== 1 || positionals[0] !== 'validate' || !values.bundle) {
    throw new Error('用法：node cli.mjs validate --bundle <资料目录> [--report <报告>] [--previous <前版报告>]');
  }
  const loaded = await loadBundle({ readFile: directoryReader(values.bundle) });
  const results = { bundle: { ok: loaded.ok, errors: loaded.errors, warnings: loaded.warnings } };
  if (values.report) {
    const readJson = async file => JSON.parse(await fs.readFile(file, 'utf8'));
    const report = await readJson(values.report);
    const previousReport = values.previous ? await readJson(values.previous) : undefined;
    results.report = validateReport(report, loaded.bundle, { previousReport });
  }
  console.log(JSON.stringify(results, null, 2));
  if (!loaded.ok || results.report?.ok === false) process.exitCode = 1;
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
