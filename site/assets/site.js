(() => {
  'use strict';

  // 演示只切换本地示意，不请求翻译接口，避免访客把营销演示当成真实翻译结果。
  const tabs = [...document.querySelectorAll('[data-demo]')];
  const instructions = {
    sentence: '选中文字 → 点「翻译」→ 查看译文',
    word: '单个英文单词 → 本地词典 → 保存与复习',
    image: '框选屏幕 → 识别文字 → 对照译图'
  };
  const selectDemo = (selected) => {
    tabs.forEach((tab) => {
      const active = tab === selected;
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
      document.getElementById(tab.getAttribute('aria-controls')).hidden = !active;
    });
    document.getElementById('demo-instruction').textContent = instructions[selected.dataset.demo];
    document.querySelector('.demo-reset').hidden = selected.dataset.demo !== 'sentence';
  };
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => selectDemo(tab));
    tab.addEventListener('keydown', (event) => {
      let next;
      if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
      if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next === undefined) return;
      event.preventDefault();
      selectDemo(tabs[next]);
      tabs[next].focus();
    });
  });

  const result = document.getElementById('sentence-result');
  let resultAnimation;
  document.querySelector('.demo-reset').addEventListener('click', () => {
    result.classList.add('is-waiting');
    document.getElementById('demo-instruction').textContent = '已选中文字，点击蓝色「翻译」按钮';
    document.querySelector('.toolbar-translate').focus({ preventScroll: true });
  });
  document.querySelector('.toolbar-translate').addEventListener('click', () => {
    // 静态首屏保留最终效果；主动重选才回到待翻译态，避免加载动画让人误以为选词会自动请求。
    result.classList.remove('is-waiting');
    resultAnimation?.cancel();
    if (!matchMedia('(prefers-reduced-motion: reduce)').matches) {
      resultAnimation = result.animate(
        [{ opacity: 0.3, transform: 'translateY(7px)' }, { opacity: 1, transform: 'none' }],
        { duration: 260, easing: 'ease-out' }
      );
    }
    document.getElementById('demo-instruction').textContent = '译文已在原文旁显示 · 此处为交互示意';
  });

  const setupThemePreview = () => {
    const screenshot = document.getElementById('app-screenshot');
    const preview = document.getElementById('theme-preview');
    const caption = document.getElementById('theme-caption');
    const player = document.querySelector('.theme-play');
    const buttons = [...document.querySelectorAll('[data-theme]')];
    const systemTheme = matchMedia('(prefers-color-scheme: dark)');
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
    const frameMs = 3600;
    let current = 0;
    let timer = null;

    const show = (index) => {
      current = index;
      const mode = buttons[index].dataset.theme;
      // v172：跟随系统是外观选择规则，预览使用当前设备偏好对应的实机截图。
      const dark = mode === 'dark' || (mode === 'system' && systemTheme.matches);
      const appearance = dark ? '深色' : '浅色';
      const source = `assets/img/main-${dark ? 'dark' : 'light'}.png`;
      if (screenshot.getAttribute('src') !== source) screenshot.src = source;
      screenshot.alt = `Fast Translation ${appearance}主窗口，左侧为功能导航，右侧为原文和译文区域`;
      preview.classList.toggle('is-dark', dark);
      caption.textContent = mode === 'system'
        ? `跟随系统 · 当前设备使用${appearance}外观`
        : dark ? '深色主题 · 暗色背景，适合夜间阅读' : '浅色主题 · 明亮背景，适合白天阅读';
      buttons.forEach((button, choice) => button.setAttribute('aria-pressed', String(choice === index)));
    };

    const stop = () => {
      if (timer === null) return;
      clearInterval(timer);
      timer = null;
      player.querySelector('span').textContent = '播放主题';
      player.querySelector('use').setAttribute('href', '#i-play');
      caption.setAttribute('aria-live', 'polite');
    };
    player.addEventListener('click', () => {
      if (timer !== null) return stop();
      caption.setAttribute('aria-live', 'off');
      player.querySelector('span').textContent = '暂停播放';
      player.querySelector('use').setAttribute('href', '#i-pause');
      show((current + 1) % buttons.length);
      timer = setInterval(() => show((current + 1) % buttons.length), frameMs);
    });
    buttons.forEach((button, index) => button.addEventListener('click', () => {
      stop();
      show(index);
    }));
    systemTheme.addEventListener('change', () => {
      if (buttons[current].dataset.theme === 'system') show(current);
    });
    document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
    reducedMotion.addEventListener('change', () => { if (reducedMotion.matches) stop(); });
  };
  setupThemePreview();

  const contact = document.getElementById('contact-dialog');
  if (typeof contact.showModal === 'function') {
    document.querySelectorAll('[data-contact]').forEach((link) => {
      link.setAttribute('aria-haspopup', 'dialog');
      link.setAttribute('aria-controls', contact.id);
      link.addEventListener('click', (event) => {
        // v172：保留原图链接，关闭脚本或用新标签打开时仍能查看二维码。
        if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        contact.showModal();
      });
    });
    contact.addEventListener('click', (event) => {
      if (event.target !== contact) return;
      const bounds = contact.getBoundingClientRect();
      if (event.clientX < bounds.left || event.clientX > bounds.right ||
          event.clientY < bounds.top || event.clientY > bounds.bottom) contact.close();
    });
  }

  // 最后再显示增强控件，脚本缺失或未执行时，静态示例与下载入口仍可完整阅读。
  document.documentElement.classList.add('js');

  const setupScrollReveal = () => {
    if (!('IntersectionObserver' in window)) return;
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    // v168：主题开关和问答按钮保持位置，避免入场位移让刚滚到这里的第一次点击落空。
    const scenes = {
      rise: '.contexts a, .section-heading > *, .speech-feature, .interface-toolbar > span, .interface-notes, ' +
            '.center-heading, .plan, .plans-note, .download-option',
      left: '.capture-feature, .learning-copy, .faq-section > div:first-child, .download-intro',
      right: '.document-feature, .learning-visual',
      iris: '.app-preview img',
      fade: '.faq-list details'
    };
    const targets = Object.entries(scenes).flatMap(([effect, selector]) =>
      [...document.querySelectorAll(selector)].map((target) => {
        target.dataset.reveal = effect;
        return target;
      })
    );
    // v168：进入时越过粘性导航和底边才播放；离开更大的缓冲区才复位，来回滚动时不会闪烁。
    const entrance = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting && entry.intersectionRatio >= 0.1) entry.target.classList.add('is-visible');
      });
    }, { threshold: 0.1, rootMargin: '-100px 0px -26px 0px' });
    const departure = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) entry.target.classList.remove('is-visible');
      });
    }, { rootMargin: '120px 0px' });
    targets.forEach((target) => {
      target.classList.add('scroll-reveal');
      const siblings = [...target.parentElement.children];
      const staggered = target.parentElement.matches('.contexts, .plans, .feature-grid, .learning-grid, .section-heading, .faq-list');
      target.style.setProperty('--reveal-delay', `${staggered ? Math.min(siblings.indexOf(target), 3) * 80 : 0}ms`);
    });
    const syncMotion = () => {
      entrance.disconnect();
      departure.disconnect();
      targets.forEach((target) => {
        target.classList.toggle('is-visible', motion.matches);
        if (!motion.matches) {
          entrance.observe(target);
          departure.observe(target);
        }
      });
    };
    motion.addEventListener('change', syncMotion);
    syncMotion();
  };
  setupScrollReveal();
})();
