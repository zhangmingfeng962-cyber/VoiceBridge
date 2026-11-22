export function validateNewPassword(input) {
  if (typeof input !== 'string') return {error: '请填写密码'};
  const value = input.normalize('NFC');
  const length = [...value].length;
  if (length < 6 || length > 128) return {error: '密码需要 6–128 个字符'};
  const kinds = [/\p{L}/u, /\p{Nd}/u, /[\p{P}\p{S}]/u];
  if (kinds.filter(kind => kind.test(value)).length < 2)
    return {error: '密码至少包含字母、数字、符号中的两类'};
  return {value};
}
