/* =====================================================================
   NekoNA Portfolio — bg3d.js
   背景の螺旋(二重ヘリックス)を WebGPU で描画する軽量モジュール
   ---------------------------------------------------------------------
   ・カーソルを動かすとモデルが傾く(慣性つき)
   ・自動でゆっくり回転
   ・ライト/ダークテーマに応じて背景色・スパイラル色を変更
     (document.dispatchEvent(new CustomEvent('themechange')) で即時反映)
   ・prefers-reduced-motion / WebGPU 非対応環境では静止・単色背景
   ・依存ライブラリなし・単体読み込みで動作(index等のbody末尾に読み込む)
   ===================================================================== */
(function () {
  'use strict';

  const DEBUG = (window.__bg3d = { ok: false, mode: 'off', error: null });

  const CAM_Z = 4.5;

  /* ───────── テーマに連動した配色 ───────── */
  const PALETTES = {
    light: {
      bg: [250 / 255, 253 / 255, 254 / 255], // #fafdfe
      deep: [0.145, 0.34, 0.86],
      light: [0.52, 0.72, 1.0],
      rim: 0.45,
    },
    dark: {
      bg: [14 / 255, 22 / 255, 32 / 255],    // #0e1620
      deep: [0.35, 0.58, 1.0],
      light: [0.68, 0.82, 1.0],
      rim: 0.6,
    },
  };
  let theme =
    document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';

  const canvas = document.createElement('canvas');
  canvas.className = 'bg3d-canvas';
  canvas.setAttribute('aria-hidden', 'true');
  document.body.appendChild(canvas);

  /* ───────── 行列ユーティリティ (列ベクトル / WebGL準拠) ───────── */
  function perspective(out, fovy, aspect, near, far) {
    const f = 1 / Math.tan(fovy / 2), nf = 1 / (near - far);
    out.fill(0);
    out[0] = f / aspect; out[5] = f;
    out[10] = (far + near) * nf; out[11] = -1;
    out[14] = 2 * far * near * nf;
    return out;
  }
  function multiply(out, a, b) {
    const o = new Float32Array(16);
    for (let c = 0; c < 4; c++) {
      for (let r = 0; r < 4; r++) {
        o[c * 4 + r] =
          a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] +
          a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
      }
    }
    out.set(o);
    return out;
  }
  function rotY(out, s, c) {
    out.set([c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]);
    return out;
  }
  function rotX(out, s, c) {
    out.set([1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]);
    return out;
  }
  function translateZ(out, z) {
    out.set([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, z, 1]);
    return out;
  }

  /* ───────── 螺旋ジオメトリ(二重ヘリックスのチューブ) ───────── */
  function buildSpiral() {
    const TURNS = 2.7, RADIUS = 1.05, HEIGHT = 3.4;
    const TUBE = 0.085, SEGS = 360, SIDES = 10, STRANDS = 2;
    const TAU = Math.PI * 2;

    const vertsPerStrand = (SEGS + 1) * (SIDES + 1);
    const verts = new Float32Array(STRANDS * vertsPerStrand * 7); // pos3 + nrm3 + t1
    const idx = [];
    let vo = 0;

    for (let k = 0; k < STRANDS; k++) {
      const phase = (k / STRANDS) * TAU;
      const base = k * vertsPerStrand;

      for (let i = 0; i <= SEGS; i++) {
        const t = i / SEGS;
        const a = phase + t * TURNS * TAU;
        const ca = Math.cos(a), sa = Math.sin(a);
        const cy = (t - 0.5) * HEIGHT;

        // 接線・従属フレーム
        const kA = TURNS * TAU;
        let tx = -sa * kA, ty = HEIGHT, tz = ca * kA;
        const tl = Math.hypot(tx, ty, tz); tx /= tl; ty /= tl; tz /= tl;
        // B = normalize(cross(T, up))
        let bx = tz, by = 0, bz = -tx;
        const bl = Math.hypot(bx, by, bz) || 1; bx /= bl; by /= bl; bz /= bl;
        // N = cross(B, T)
        const nx = by * tz - bz * ty, ny = bz * tx - bx * tz, nz = bx * ty - by * tx;

        const taper = Math.pow(Math.sin(Math.PI * t), 0.45) * TUBE;

        for (let j = 0; j <= SIDES; j++) {
          const phi = (j / SIDES) * TAU;
          const cp = Math.cos(phi), sp = Math.sin(phi);
          const dx = cp * nx + sp * bx, dy = cp * ny + sp * by, dz = cp * nz + sp * bz;
          verts[vo++] = RADIUS * ca + taper * dx;
          verts[vo++] = cy + taper * dy;
          verts[vo++] = RADIUS * sa + taper * dz;
          verts[vo++] = dx; verts[vo++] = dy; verts[vo++] = dz;
          verts[vo++] = t;
        }
      }
      for (let i = 0; i < SEGS; i++) {
        for (let j = 0; j < SIDES; j++) {
          const a0 = base + i * (SIDES + 1) + j;
          const b0 = a0 + SIDES + 1;
          idx.push(a0, b0, a0 + 1, a0 + 1, b0, b0 + 1);
        }
      }
    }
    return { verts, indices: new Uint32Array(idx) };
  }

  /* ───────── シェーダ(CAM_Z_F / DEEP_F / LIGHT_F / RIM_F を実行時に置換) ───────── */
  const WGSL_SRC = /* wgsl */ `
    struct U {
      mvp    : mat4x4<f32>,
      model  : mat4x4<f32>,
      params : vec4<f32>, // x: time
    };
    @group(0) @binding(0) var<uniform> u : U;

    struct VSOut {
      @builtin(position) pos  : vec4f,
      @location(0) world      : vec3f,
      @location(1) normal     : vec3f,
      @location(2) t          : f32,
    };

    @vertex
    fn vs(@location(0) p : vec3f, @location(1) n : vec3f, @location(2) t : f32) -> VSOut {
      var o : VSOut;
      let world = u.model * vec4f(p, 1.0);
      o.pos = u.mvp * vec4f(p, 1.0);
      o.world = world.xyz;
      o.normal = (u.model * vec4f(n, 0.0)).xyz;
      o.t = t;
      return o;
    }

    @fragment
    fn fs(in : VSOut) -> @location(0) vec4f {
      let n = normalize(in.normal);
      let v = normalize(vec3f(0.0, 0.0, CAM_Z_F) - in.world);
      let key  = normalize(vec3f(0.55, 0.75, 0.55));
      let fill = normalize(vec3f(-0.6, -0.25, 0.5));
      let diff = max(dot(n, key), 0.0) * 0.85 + max(dot(n, fill), 0.0) * 0.35;
      let rim  = pow(1.0 - max(dot(n, v), 0.0), 2.6) * RIM_F;

      let base = mix(DEEP_F, LIGHT_F, smoothstep(0.0, 1.0, in.t));
      let shimmer = 0.94 + 0.06 * sin(u.params.x * 0.7 + in.t * 8.0);

      var col = base * (0.42 + diff * 0.9) * shimmer + vec3f(rim);
      return vec4f(col, 1.0);
    }
  `;
  function wgslFor(p) {
    return WGSL_SRC
      .replace('CAM_Z_F', String(CAM_Z))
      .replace('RIM_F', String(p.rim))
      .replace(
        'DEEP_F',
        `vec3f(${p.deep[0].toFixed(3)}, ${p.deep[1].toFixed(3)}, ${p.deep[2].toFixed(3)})`
      )
      .replace(
        'LIGHT_F',
        `vec3f(${p.light[0].toFixed(3)}, ${p.light[1].toFixed(3)}, ${p.light[2].toFixed(3)})`
      );
  }

  /* ───────── 初期化 ───────── */
  async function init() {
    if (!navigator.gpu) { DEBUG.mode = 'fallback'; DEBUG.error = 'no-webgpu'; return; }

    const adapter = await navigator.gpu.requestAdapter();
    if (!adapter) { DEBUG.mode = 'fallback'; DEBUG.error = 'no-adapter'; return; }
    const device = await adapter.device;
    device.lost && device.lost.then(() => { DEBUG.ok = false; DEBUG.mode = 'off'; });

    const ctx = canvas.getContext('webgpu');
    if (!ctx) { DEBUG.mode = 'fallback'; DEBUG.error = 'no-context'; return; }
    const format = navigator.gpu.getPreferredCanvasFormat();
    ctx.configure({ device, format, alphaMode: 'opaque' });

    /* バッファ */
    const { verts, indices } = buildSpiral();
    const vbuf = device.createBuffer({ size: verts.byteLength, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(vbuf, 0, verts);
    const ibuf = device.createBuffer({ size: indices.byteLength, usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(ibuf, 0, indices);

    const uni = device.createBuffer({ size: 144, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });

    /* テーマごとのパイプライン(シェーダの配色が異なるため2つ保持) */
    const pipelines = {};
    const binds = {};
    for (const key of ['light', 'dark']) {
      const module = device.createShaderModule({ code: wgslFor(PALETTES[key]) });
      const pipeline = device.createRenderPipeline({
        layout: 'auto',
        vertex: {
          module, entryPoint: 'vs',
          buffers: [{
            arrayStride: 28,
            attributes: [
              { shaderLocation: 0, offset: 0, format: 'float32x3' },
              { shaderLocation: 1, offset: 12, format: 'float32x3' },
              { shaderLocation: 2, offset: 24, format: 'float32' },
            ],
          }],
        },
        fragment: { module, entryPoint: 'fs', targets: [{ format }] },
        primitive: { topology: 'triangle-list', cullMode: 'none' },
        depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' },
        multisample: { count: 4 },
      });
      pipelines[key] = pipeline;
      binds[key] = device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [{ binding: 0, resource: { buffer: uni } }],
      });
    }

    /* MSAA + 深度(リサイズごとに再生成) */
    let msaaTex = null, depthTex = null;
    function makeTargets(w, h) {
      msaaTex && msaaTex.destroy();
      depthTex && depthTex.destroy();
      msaaTex = device.createTexture({
        size: [w, h], sampleCount: 4, format,
        usage: GPUTextureUsage.RENDER_ATTACHMENT,
      });
      depthTex = device.createTexture({
        size: [w, h], sampleCount: 4, format: 'depth24plus',
        usage: GPUTextureUsage.RENDER_ATTACHMENT,
      });
    }

    const dpr = () => Math.min(window.devicePixelRatio || 1, 1.75);
    function resize() {
      const w = Math.max(1, Math.floor(window.innerWidth * dpr()));
      const h = Math.max(1, Math.floor(window.innerHeight * dpr()));
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w; canvas.height = h;
        makeTargets(w, h);
        needsRedraw = true;
      }
    }
    resize();
    window.addEventListener('resize', resize, { passive: true });

    /* マウス/タッチ → ティルト(慣性) */
    let mx = 0, my = 0, tmx = 0, tmy = 0;
    window.addEventListener('pointermove', (e) => {
      tmx = (e.clientX / window.innerWidth) * 2 - 1;
      tmy = (e.clientY / window.innerHeight) * 2 - 1;
    }, { passive: true });

    /* 行列 */
    const data = new Float32Array(36);
    const proj = new Float32Array(16), view = new Float32Array(16);
    const mModel = new Float32Array(16), tmpA = new Float32Array(16), tmpB = new Float32Array(16);

    function updateMatrices(spin, tx, ty) {
      perspective(proj, Math.PI / 4, canvas.width / canvas.height, 0.1, 100);
      translateZ(view, -CAM_Z);
      const s1 = Math.sin(spin + tx), c1 = Math.cos(spin + tx);
      const s2 = Math.sin(ty), c2 = Math.cos(ty);
      rotY(tmpA, s1, c1);
      rotX(tmpB, s2, c2);
      multiply(mModel, tmpA, tmpB);   // model = RY * RX
      multiply(tmpA, view, mModel);   // view * model
      multiply(tmpB, proj, tmpA);     // mvp
      data.set(tmpB, 0);
      data.set(mModel, 16);
    }

    function renderOnce(spin, tiltX, tiltY, timeSec) {
      updateMatrices(spin, tiltX, tiltY);
      data[32] = timeSec;
      device.queue.writeBuffer(uni, 0, data);

      const pal = PALETTES[theme];
      const encoder = device.createCommandEncoder();
      const pass = encoder.beginRenderPass({
        colorAttachments: [{
          view: msaaTex.createView(),
          resolveTarget: ctx.getCurrentTexture().createView(),
          clearValue: { r: pal.bg[0], g: pal.bg[1], b: pal.bg[2], a: 1 },
          loadOp: 'clear', storeOp: 'store',
        }],
        depthStencilAttachment: {
          view: depthTex.createView(),
          depthClearValue: 1.0, depthLoadOp: 'clear', depthStoreOp: 'store',
        },
      });
      pass.setPipeline(pipelines[theme]);
      pass.setBindGroup(0, binds[theme]);
      pass.setVertexBuffer(0, vbuf);
      pass.setIndexBuffer(ibuf, 'uint32');
      pass.drawIndexed(indices.length);
      pass.end();
      device.queue.submit([encoder.finish()]);
      DEBUG.ok = true; DEBUG.mode = 'webgpu';
    }

    let spin = 0, last = performance.now(), raf = 0, running = false, needsRedraw = false;

    function frame(now) {
      const dt = Math.min((now - last) / 1000, 0.05);
      last = now;
      spin += dt * 0.16;
      // 慣性フォロー
      const k = Math.min(1, dt * 4.5);
      mx += (tmx - mx) * k;
      my += (tmy - my) * k;

      renderOnce(spin, mx * 0.55, -my * 0.35, now / 1000);

      if (running) raf = requestAnimationFrame(frame);
    }

    function start() {
      if (running) return;
      running = true;
      last = performance.now();
      raf = requestAnimationFrame(frame);
    }
    function stop() {
      running = false;
      if (raf) cancelAnimationFrame(raf), raf = 0;
    }

    document.addEventListener('visibilitychange', () => {
      document.hidden ? stop() : start();
    });

    /* テーマ変更を即時反映 */
    document.addEventListener('themechange', () => {
      theme = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
      // アニメ中は次フレームで自動反映
    });

    start();
  }

  init().catch((err) => {
    DEBUG.error = String(err && err.message || err);
    DEBUG.mode = 'fallback';
  });
})();
