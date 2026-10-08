// Only the startup handshake is written to stdout. SDK diagnostics may contain secrets.
for (const method of ['log', 'info', 'warn', 'error', 'debug', 'trace']) console[method] = () => {};
process.env.ENABLE_GENERAL_UNBLOCK = 'false';
process.env.QISHUI_ENABLE_DECRYPT = 'false';
process.env.DEBUG = 'false';
process.on('uncaughtException', () => { process.stderr.write('Music gateway fatal error.\n'); process.exit(1); });
process.on('unhandledRejection', () => { process.stderr.write('Music gateway fatal error.\n'); process.exit(1); });
const { main } = await import('./src/index.js');
try { await main(); } catch { process.stderr.write('Music gateway startup failed.\n'); process.exitCode = 1; }
