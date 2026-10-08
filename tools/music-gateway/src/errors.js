export class GatewayError extends Error {
  constructor(code, message, status = 502, retryable = false) {
    super(message); this.code = code; this.status = status; this.retryable = retryable;
  }
}

export function transportError(error) {
  if (error instanceof GatewayError) return error;
  const codes = [error?.code, error?.cause?.code];
  if (error?.name === 'AbortError' || error?.name === 'TimeoutError' || codes.some(c => ['ERR_CANCELED','ECONNABORTED','ETIMEDOUT','UND_ERR_CONNECT_TIMEOUT'].includes(c)))
    return new GatewayError('timeout', '平台请求已取消或超时', 504, true);
  if (codes.some(c => /CERT|TLS|SSL|SELF_SIGNED/.test(c || '')))
    return new GatewayError('tls_error', '平台 HTTPS 证书或 TLS 验证失败', 502, true);
  const status = Number(error?.response?.status || error?.details?.status);
  if (status === 429) return new GatewayError('rate_limited', '平台暂时限制请求频率', 429, true);
  if (status === 401 || status === 403) return new GatewayError('authentication_required', '平台拒绝游客请求或要求认证', status, false);
  if (status >= 400) return new GatewayError('upstream_http_error', `平台返回 HTTP ${status}`, 502, status >= 500);
  return new GatewayError('network_error', '无法连接平台服务', 502, true);
}

export function checkBusiness(body, { success = [0, 200], key = 'code' } = {}) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new GatewayError('invalid_upstream_response', '平台返回了无效数据');
  const value = body[key];
  if (value === undefined) return body;
  if (success.includes(Number(value))) return body;
  const code = String(value);
  if (['301','302','401','403','-111','104','1008','20003','1013','1002'].includes(code)) throw new GatewayError('authentication_required', '平台要求登录或游客认证已失效', 403);
  if (['429','509','-460'].includes(code)) throw new GatewayError('rate_limited', '平台暂时限制请求频率', 429, true);
  throw new GatewayError('upstream_business_error', `平台业务请求失败（${code.slice(0, 20)}）`, 502);
}
