/**
 * Shared bits for reading a function's parameter schema and rendering inputs
 * for it. Lives apart from FunctionsPage so both the catalogue list and the
 * test panel can use them without importing each other.
 */

/** One top-level parameter, as the row preview and the test form both need it. */
export interface ParamSpec {
  name: string;
  type?: string;
  enumValues?: unknown[];
  required: boolean;
  description?: string;
}

/**
 * Read the top-level `properties` of a parameters JSON Schema.
 *
 * Deliberately shallow — the same subset the harness honours when it
 * destructures `args`, and the same subset the server validates. Nested
 * schemas, oneOf/anyOf and formats are ignored; the test form falls back to
 * raw JSON whenever a parameter is anything but a flat primitive.
 */
export function parseParams(schemaJson: string): ParamSpec[] {
  try {
    const doc = JSON.parse(schemaJson);
    const props = doc?.properties;
    if (!props || typeof props !== "object") return [];
    const required: string[] = Array.isArray(doc?.required) ? doc.required : [];
    return Object.keys(props).map((name) => {
      const p = props[name] ?? {};
      return {
        name,
        type: typeof p.type === "string" ? p.type : undefined,
        enumValues: Array.isArray(p.enum) ? p.enum : undefined,
        required: required.includes(name),
        description: typeof p.description === "string" ? p.description : undefined,
      };
    });
  } catch {
    return [];
  }
}

/** True when every parameter is a flat primitive we can render an input for. */
export function isSimpleParamSet(params: ParamSpec[]): boolean {
  return params.every(
    (p) => p.type === undefined || ["string", "number", "integer", "boolean"].includes(p.type)
  );
}

/** One-line `name: type, name: type` signature for the catalogue row. */
export function extractParamPreview(schemaJson: string): string {
  return parseParams(schemaJson)
    .map((p) => `${p.name}: ${p.type ?? "any"}`)
    .join(", ");
}

export function prettifyJson(s: string): string {
  try {
    return JSON.stringify(JSON.parse(s), null, 2);
  } catch {
    return s;
  }
}

export function LabeledInput({
  label,
  value,
  onChange,
  placeholder,
  multiline,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  multiline?: boolean;
}) {
  const Tag = multiline ? "textarea" : "input";
  return (
    <div style={{ marginTop: 12 }}>
      <div style={{ fontSize: 11, color: "var(--text-3)", marginBottom: 6, fontFamily: "var(--f-mono)" }}>
        {label}
      </div>
      <Tag
        value={value}
        onChange={(e) => onChange((e.target as HTMLInputElement | HTMLTextAreaElement).value)}
        placeholder={placeholder}
        rows={multiline ? 2 : undefined}
        style={{
          width: "100%",
          padding: "10px 12px",
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 8,
          color: "var(--text-1)",
          fontSize: 13,
          resize: multiline ? "vertical" : undefined,
        }}
      />
    </div>
  );
}

export function LabeledSelect({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: { value: string; label: string }[];
  onChange: (v: string) => void;
}) {
  return (
    <div style={{ marginTop: 12 }}>
      <div style={{ fontSize: 11, color: "var(--text-3)", marginBottom: 6, fontFamily: "var(--f-mono)" }}>
        {label}
      </div>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        style={{
          width: "100%",
          padding: "10px 12px",
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 8,
          color: "var(--text-1)",
          fontSize: 13,
        }}
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}
