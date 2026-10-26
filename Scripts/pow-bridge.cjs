'use strict';
const { solvePOW } = require('./pow.cjs');
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', data => input += data);
process.stdin.on('end', async () => {
  try {
    const { challenge, wasmUrl } = JSON.parse(input);
    const url = new URL(wasmUrl);
    if (url.protocol !== 'https:' || url.hostname !== 'fe-static.deepseek.com') throw new Error('Invalid WASM host');
    if (challenge.algorithm !== 'DeepSeekHashV1') throw new Error('Unsupported PoW algorithm');
    console.log(await solvePOW(challenge, wasmUrl));
  } catch { console.error('PoW failed'); process.exitCode = 1; }
});
