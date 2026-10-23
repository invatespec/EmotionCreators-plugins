import Ajv from 'ajv';
import common from '../../ec-scene-review/Reference/schemas/common.schema.json' with { type: 'json' };
import bundle from '../../ec-scene-review/Reference/schemas/bundle.schema.json' with { type: 'json' };
import report from '../../ec-scene-review/Reference/schemas/report.schema.json' with { type: 'json' };

const ajv = new Ajv({ allErrors: true, strict: true, strictRequired: false });
ajv.addSchema(common);
const validators = { bundle: ajv.compile(bundle), report: ajv.compile(report) };

export function validateShape(kind, value) {
  const validate = validators[kind];
  if (validate(value)) return [];
  return validate.errors.map(error => ({
    code: 'schema',
    path: error.instancePath,
    message: error.message,
    details: error.params,
  }));
}
