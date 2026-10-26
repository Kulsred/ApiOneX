'use strict';
const { solvePOW } = require('./pow.cjs');
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', data => input += data);
process.stdin.on('end', async () => {
  try {
    const { challenge, wasmUrl } = JSON.parse(input);
    const url = new URL(wasmUrl);
    if (url.protocol !== 'https:' || url.hostname !== 'fe-static.deepseek.com') throw Object.assign(new Error(), { code: 'POW_INVALID_INPUT' });
    if (challenge.algorithm !== 'DeepSeekHashV1') throw Object.assign(new Error(), { code: 'POW_UNSUPPORTED_ALGORITHM' });
    if (typeof challenge.challenge !== 'string' || typeof challenge.salt !== 'string' || !Number.isFinite(challenge.expire_at) || !Number.isFinite(challenge.difficulty) || challenge.difficulty <= 0)
      throw Object.assign(new Error(), { code: 'POW_INVALID_INPUT' });
    console.log(await solvePOW(challenge, wasmUrl));
  } catch (e) {
    const cause = e.cause?.code || e.code || '';
    let code = /^POW_[A-Z_]+$/.test(cause) ? cause : 'POW_WORKER';
    if (['SELF_SIGNED_CERT_IN_CHAIN', 'DEPTH_ZERO_SELF_SIGNED_CERT', 'UNABLE_TO_VERIFY_LEAF_SIGNATURE', 'UNABLE_TO_GET_ISSUER_CERT_LOCALLY', 'CERT_HAS_EXPIRED'].includes(cause)) code = 'POW_TLS_CERTIFICATE';
    else if (e.name === 'TimeoutError' || e.name === 'AbortError') code = 'POW_TIMEOUT';
    else if (e.message === 'fetch failed' || e.message?.includes('could not fetch WASM')) code = 'POW_DOWNLOAD';
    else if (e instanceof WebAssembly.CompileError || e instanceof WebAssembly.LinkError || e instanceof WebAssembly.RuntimeError || e.message?.includes('is not a function')) code = 'POW_WASM_ABI';
    else if (e.message === 'POW failed') code = 'POW_NO_SOLUTION';
    console.error(JSON.stringify({ code }));
    process.exitCode = 1;
  }
});
