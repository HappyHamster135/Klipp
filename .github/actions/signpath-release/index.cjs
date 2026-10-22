'use strict';

try { require('../../../tools/signpath/build.cjs').build(); }
catch (error) { console.error(error.message); process.exitCode = 1; }
