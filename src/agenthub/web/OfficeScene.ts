// Cena do escritório. Cada estação corresponde a um agente real; a cena só
// desenha o que recebe do serviço de eventos.

import Phaser from 'phaser';
import type { AgentState, AgentView } from './hub';
import { STATE_LABEL, errorShort } from './hub';
import { CHAR_VARIANTS, FRAME, buildTextures, variantFor } from './sprites';

const COLS = 6;
const CELL_W = 92;
const CELL_H = 88;
const SIDE = 26;
const WALL_H = 48;
const TOP_PAD = 36;
const LOUNGE_H = 60;
const ROOM_W = SIDE * 2 + COLS * CELL_W;
const MIN_ROWS = 2;

const FONT = '"VT323", "Courier New", monospace';

const SCREEN_TINT: Record<AgentState, number> = {
  working: 0x9fe8a0,
  waiting: 0x9fc8e8,
  done: 0xf6d78a,
  error: 0xf08070,
  disconnected: 0x000000,
  unknown: 0xb0a8c0,
};

const LABEL_COLOR: Record<AgentState, string> = {
  working: '#a8e6a0',
  waiting: '#a8d0f0',
  done: '#f6d78a',
  error: '#ff9a88',
  disconnected: '#9aa6aa',
  unknown: '#cdbfe6',
};

interface Station {
  key: string;
  parentKey: string | null;
  container: Phaser.GameObjects.Container;
  ring: Phaser.GameObjects.Graphics;
  char: Phaser.GameObjects.Sprite;
  screen: Phaser.GameObjects.Image;
  bubble: Phaser.GameObjects.Image;
  star: Phaser.GameObjects.Image;
  name: Phaser.GameObjects.Text;
  role: Phaser.GameObjects.Text;
  status: Phaser.GameObjects.Text;
  variant: number;
  visual: string;
  tweens: Phaser.Tweens.Tween[];
}

function roomHeight(rows: number): number {
  return WALL_H + TOP_PAD + rows * CELL_H + LOUNGE_H;
}

function slotPosition(index: number): { x: number; y: number } {
  const col = index % COLS;
  const row = Math.floor(index / COLS);
  return { x: SIDE + col * CELL_W + CELL_W / 2, y: WALL_H + TOP_PAD + row * CELL_H + 30 };
}

function shorten(text: string, max: number): string {
  return text.length > max ? text.slice(0, max - 1) + '…' : text;
}

export class OfficeScene extends Phaser.Scene {
  /** Chamado quando a pessoa escolhe um agente (ou nenhum). */
  onSelect: (key: string | null) => void = () => {};

  private stations = new Map<string, Station>();
  private order: string[] = [];
  private pending: AgentView[] | null = null;
  private ready = false;
  private rows = 0;
  private room!: Phaser.GameObjects.Container;
  private links!: Phaser.GameObjects.Graphics;
  private selectedKey: string | null = null;
  private hoveredKey: string | null = null;
  private dragDistance = 0;
  private movedByUser = false;
  private cursors: Phaser.Types.Input.Keyboard.CursorKeys | null = null;

  constructor() {
    super('office');
  }

  create(): void {
    buildTextures(this);
    this.createAnimations();
    this.room = this.add.container(0, 0).setDepth(0);
    this.links = this.add.graphics().setDepth(1);
    this.drawRoom(MIN_ROWS);
    this.setupCamera();
    this.ready = true;
    if (this.pending) {
      const list = this.pending;
      this.pending = null;
      this.setAgents(list);
    }
  }

  update(): void {
    if (!this.cursors) return;
    const camera = this.cameras.main;
    const step = 5 / camera.zoom + 1;
    if (this.cursors.left.isDown) camera.scrollX -= step;
    if (this.cursors.right.isDown) camera.scrollX += step;
    if (this.cursors.up.isDown) camera.scrollY -= step;
    if (this.cursors.down.isDown) camera.scrollY += step;
    if (this.cursors.left.isDown || this.cursors.right.isDown || this.cursors.up.isDown || this.cursors.down.isDown) {
      this.movedByUser = true;
    }
  }

  // ------------------------------------------------------------ API pública

  setAgents(list: AgentView[]): void {
    if (!this.ready) {
      this.pending = list;
      return;
    }
    const incoming = new Map(list.map((agent) => [agent.key, agent]));

    // Mantém a mesa de quem já estava; quem chega ocupa a próxima mesa livre.
    this.order = this.order.filter((key) => incoming.has(key));
    for (const agent of list) if (!this.order.includes(agent.key)) this.order.push(agent.key);

    for (const [key, station] of this.stations) {
      if (!incoming.has(key)) {
        station.tweens.forEach((tween) => tween.remove());
        station.container.destroy();
        this.stations.delete(key);
      }
    }

    const rows = Math.max(MIN_ROWS, Math.ceil(this.order.length / COLS));
    if (rows !== this.rows) {
      this.drawRoom(rows);
      if (!this.movedByUser) this.fitCamera();
    }

    this.order.forEach((key, index) => {
      const agent = incoming.get(key);
      if (!agent) return;
      const { x, y } = slotPosition(index);
      let station = this.stations.get(key);
      if (!station) {
        station = this.createStation(agent, x, y);
        this.stations.set(key, station);
      }
      station.container.setPosition(x, y).setDepth(y);
      this.updateStation(station, agent);
    });

    this.drawLinks();
    this.drawRings();
  }

  setSelected(key: string | null): void {
    this.selectedKey = key;
    if (this.ready) this.drawRings();
  }

  focus(key: string): void {
    const station = this.stations.get(key);
    if (!station) return;
    this.movedByUser = true;
    this.cameras.main.pan(station.container.x, station.container.y, 320, 'Sine.easeInOut');
  }

  // ---------------------------------------------------------------- cenário

  private drawRoom(rows: number): void {
    this.rows = rows;
    this.room.removeAll(true);
    const height = roomHeight(rows);
    const add = <T extends Phaser.GameObjects.GameObject>(object: T): T => {
      this.room.add(object);
      return object;
    };

    add(this.add.tileSprite(0, WALL_H, ROOM_W, height - WALL_H, 'floor').setOrigin(0));
    add(this.add.tileSprite(0, 0, ROOM_W, WALL_H, 'wall').setOrigin(0));

    const shade = add(this.add.graphics());
    shade.fillStyle(0x1e2228, 0.25).fillRect(0, WALL_H, ROOM_W, 4);

    // Luz das janelas no carpete.
    const light = add(this.add.graphics());
    const windows = [0.13, 0.38, 0.62, 0.87].map((part) => Math.round(ROOM_W * part));
    for (const wx of windows) {
      light.fillStyle(0xeaf4ff, 0.1);
      light.fillPoints(
        [
          new Phaser.Math.Vector2(wx - 17, WALL_H + 3),
          new Phaser.Math.Vector2(wx + 17, WALL_H + 3),
          new Phaser.Math.Vector2(wx + 36, WALL_H + 52),
          new Phaser.Math.Vector2(wx - 4, WALL_H + 52),
        ],
        true,
      );
      add(this.add.image(wx, 3, 'window').setOrigin(0.5, 0));
    }
    add(this.add.image(Math.round(ROOM_W * 0.255), 9, 'board').setOrigin(0.5, 0));
    add(this.add.image(Math.round(ROOM_W * 0.745), 9, 'board').setOrigin(0.5, 0).setFlipX(true));
    add(this.add.image(ROOM_W / 2, 12, 'clock').setOrigin(0.5, 0));

    // Móveis encostados na parede: copa à esquerda, arquivos e impressora à direita.
    add(this.add.image(20, WALL_H + 13, 'coffee').setOrigin(0.5, 1));
    add(this.add.image(40, WALL_H + 13, 'cooler').setOrigin(0.5, 1));
    add(this.add.image(ROOM_W - 14, WALL_H + 14, 'cabinet').setOrigin(0.5, 1));
    add(this.add.image(ROOM_W - 33, WALL_H + 14, 'cabinet').setOrigin(0.5, 1));
    add(this.add.image(ROOM_W - 57, WALL_H + 13, 'printer').setOrigin(0.5, 1));

    // Sala de reunião, na frente do escritório.
    const loungeY = height - LOUNGE_H / 2 - 2;
    add(this.add.image(ROOM_W / 2, loungeY, 'meeting'));
    add(this.add.image(18, height - 9, 'plant').setOrigin(0.5, 1));
    add(this.add.image(ROOM_W - 18, height - 9, 'plant').setOrigin(0.5, 1));
    add(this.add.image(ROOM_W / 2 - 86, height - 9, 'plant').setOrigin(0.5, 1));
    add(this.add.image(ROOM_W / 2 + 86, height - 9, 'plant').setOrigin(0.5, 1));

    // Moldura do cômodo.
    const frame = add(this.add.graphics());
    frame.fillStyle(0x3a3f45, 1);
    frame.fillRect(-4, -4, ROOM_W + 8, 4);
    frame.fillRect(-4, -4, 4, height + 8);
    frame.fillRect(ROOM_W, -4, 4, height + 8);
    frame.fillRect(-4, height, ROOM_W + 8, 4);
  }

  private createAnimations(): void {
    for (let variant = 0; variant < CHAR_VARIANTS; variant += 1) {
      const key = `char${variant}`;
      const frames = (indexes: number[]) => indexes.map((frame) => ({ key, frame }));
      const make = (name: string, indexes: number[], frameRate: number) => {
        if (!this.anims.exists(`${key}_${name}`)) {
          this.anims.create({ key: `${key}_${name}`, frames: frames(indexes), frameRate, repeat: -1 });
        }
      };
      make('idle', [FRAME.idle, FRAME.idle, FRAME.idle, FRAME.idle, FRAME.blink], 2.5);
      make('type', [FRAME.typeA, FRAME.typeB], 9);
      make('typeSlow', [FRAME.typeA, FRAME.typeB, FRAME.typeA, FRAME.idle], 4);
      make('wait', [FRAME.idle, FRAME.lookAside, FRAME.lookAside, FRAME.idle, FRAME.blink], 1.6);
      make('cheer', [FRAME.cheer, FRAME.smile, FRAME.cheer, FRAME.smile, FRAME.smile, FRAME.smile], 3);
      make('panic', [FRAME.panic, FRAME.shock], 5);
    }
  }

  // --------------------------------------------------------------- estações

  private createStation(agent: AgentView, x: number, y: number): Station {
    const variant = variantFor(agent.key);
    const container = this.add.container(x, y);
    const ring = this.add.graphics();
    const shadow = this.add.image(0, 21, 'shadow');
    // Divisória da baia, atrás da cadeira.
    const partition = this.add.image(0, -6, 'partition').setOrigin(0.5, 1);
    const chair = this.add.image(5, -3, 'chair').setOrigin(0.5, 1);
    const desk = this.add.image(0, 8, 'desk');
    const char = this.add.sprite(5, 1, `char${variant}`, FRAME.idle).setOrigin(0.5, 1);
    const monitor = this.add.image(-13, 2, 'monitor').setOrigin(0.5, 1);
    const screen = this.add.image(-20, -12, 'screen').setOrigin(0, 0);
    const bubble = this.add.image(6, -19, 'b_wait').setOrigin(0.5, 1).setVisible(false);
    const star = this.add.image(0, 28, 'star').setVisible(false);

    const textStyle = { fontFamily: FONT, fontSize: '11px', color: '#f3e7c9', stroke: '#2b1d1f', strokeThickness: 2 };
    const name = this.add.text(0, 22, '', textStyle).setOrigin(0.5, 0).setResolution(4);
    const role = this.add.text(0, 31, '', { ...textStyle, fontSize: '9px', color: '#c9d4d6' }).setOrigin(0.5, 0).setResolution(4);
    const status = this.add.text(0, 39, '', { ...textStyle, fontSize: '10px' }).setOrigin(0.5, 0).setResolution(4);

    const zone = this.add.zone(0, 4, 80, 76).setInteractive({ useHandCursor: true });
    zone.on('pointerover', () => {
      this.hoveredKey = agent.key;
      this.drawRings();
    });
    zone.on('pointerout', () => {
      if (this.hoveredKey === agent.key) this.hoveredKey = null;
      this.drawRings();
    });
    zone.on('pointerup', () => {
      if (this.dragDistance < 6) this.onSelect(agent.key);
    });

    container.add([partition, ring, shadow, chair, desk, char, monitor, screen, bubble, star, name, role, status, zone]);
    return {
      key: agent.key,
      parentKey: agent.parentKey,
      container,
      ring,
      char,
      screen,
      bubble,
      star,
      name,
      role,
      status,
      variant,
      visual: '',
      tweens: [],
    };
  }

  private updateStation(station: Station, agent: AgentView): void {
    station.parentKey = agent.parentKey;
    const label = shorten(agent.nickname, 18);
    const roleText = shorten(agent.name, 20);
    if (station.role.text !== roleText) station.role.setText(roleText);
    if (station.name.text !== label) station.name.setText(label);
    station.name.setColor(agent.kind === 'main' ? '#f6c84a' : '#f3e7c9');
    station.star.setVisible(agent.kind === 'main');
    station.star.setPosition(-station.name.width / 2 - 6, 28);

    // Com erro, a etiqueta mostra o motivo curto (ex.: "Limite de uso").
    const statusText = agent.state === 'error' ? shorten(errorShort(agent.lastError), 22) : STATE_LABEL[agent.state];
    if (station.status.text !== statusText) station.status.setText(statusText);
    station.status.setColor(LABEL_COLOR[agent.state]);

    const visual = `${agent.state}|${agent.activity ?? ''}`;
    if (visual === station.visual) return;
    station.visual = visual;
    this.applyVisual(station, agent);
  }

  private applyVisual(station: Station, agent: AgentView): void {
    const { char, screen, bubble } = station;
    station.tweens.forEach((tween) => tween.remove());
    station.tweens = [];
    char.setVisible(true).setAlpha(1);
    char.setPosition(5, 1);
    bubble.setVisible(false).setPosition(6, -19).setAngle(0);
    screen.setVisible(true).setAlpha(1).setTint(SCREEN_TINT[agent.state]);
    station.name.setAlpha(1);

    const anim = (name: string) => char.play(`char${station.variant}_${name}`, true);
    const showBubble = (key: string) => bubble.setTexture(key).setVisible(true);
    const float = () =>
      station.tweens.push(
        this.tweens.add({ targets: bubble, y: -21, duration: 700, yoyo: true, repeat: -1, ease: 'Sine.easeInOut' }),
      );

    switch (agent.state) {
      case 'working': {
        const bubbles: Record<string, string> = {
          coding: 'b_code',
          analyzing: 'b_search',
          testing: 'b_test',
          delegating: 'b_spawn',
        };
        anim(agent.activity === 'analyzing' ? 'typeSlow' : 'type');
        const key = agent.activity ? bubbles[agent.activity] : undefined;
        if (key) {
          showBubble(key);
          float();
        }
        station.tweens.push(
          this.tweens.add({ targets: screen, alpha: 0.72, duration: 260, yoyo: true, repeat: -1 }),
        );
        break;
      }
      case 'waiting':
        anim('wait');
        showBubble('b_wait');
        float();
        break;
      case 'done':
        anim('cheer');
        showBubble('b_done');
        float();
        break;
      case 'error':
        anim('panic');
        showBubble('b_error');
        station.tweens.push(
          this.tweens.add({ targets: bubble, angle: { from: -10, to: 10 }, duration: 110, yoyo: true, repeat: -1 }),
          this.tweens.add({ targets: screen, alpha: 0.25, duration: 220, yoyo: true, repeat: -1 }),
        );
        break;
      case 'disconnected':
        // Ninguém na mesa e monitor apagado.
        char.stop().setVisible(false);
        screen.setVisible(false);
        station.name.setAlpha(0.6);
        break;
      case 'unknown':
        anim('idle');
        char.setAlpha(0.55);
        showBubble('b_unknown');
        float();
        break;
    }
  }

  private drawRings(): void {
    for (const station of this.stations.values()) {
      const selected = station.key === this.selectedKey;
      const hovered = station.key === this.hoveredKey;
      station.ring.clear();
      if (!selected && !hovered) continue;
      station.ring.lineStyle(1, selected ? 0xf6c84a : 0xf3e7c9, selected ? 1 : 0.6);
      station.ring.strokeRect(-40, -36, 80, 88);
      if (selected) {
        station.ring.fillStyle(0xf6c84a, 1);
        for (const [x, y] of [[-41, -37], [38, -37], [-41, 50], [38, 50]]) station.ring.fillRect(x, y, 3, 3);
      }
    }
  }

  /** Liga cada subagente à mesa de quem o lançou, com uma trilha pontilhada. */
  private drawLinks(): void {
    this.links.clear();
    this.links.fillStyle(0xf3e7c9, 0.55);
    for (const station of this.stations.values()) {
      if (!station.parentKey) continue;
      const parent = this.stations.get(station.parentKey);
      if (!parent) continue;
      const from = new Phaser.Math.Vector2(parent.container.x, parent.container.y + 53);
      const to = new Phaser.Math.Vector2(station.container.x, station.container.y + 53);
      const mid = new Phaser.Math.Vector2((from.x + to.x) / 2, Math.max(from.y, to.y) + 6);
      const curve = new Phaser.Curves.QuadraticBezier(from, mid, to);
      const steps = Math.max(4, Math.floor(curve.getLength() / 5));
      for (let i = 0; i <= steps; i += 1) {
        const point = curve.getPoint(i / steps);
        this.links.fillRect(Math.round(point.x), Math.round(point.y), 1, 1);
      }
    }
  }

  // ------------------------------------------------------------------ câmera

  private fitCamera(): void {
    const camera = this.cameras.main;
    const height = roomHeight(this.rows);
    const zoom = Math.min(camera.width / (ROOM_W + 24), camera.height / (height + 24));
    // Passos de 0,25 mantêm o pixel art nítido sem desperdiçar o palco.
    camera.setZoom(Phaser.Math.Clamp(Math.floor(zoom * 4) / 4, 1, 6));
    camera.centerOn(ROOM_W / 2, height / 2);
  }

  private setupCamera(): void {
    this.fitCamera();
    this.cursors = this.input.keyboard?.createCursorKeys() ?? null;

    this.input.on('pointerdown', () => {
      this.dragDistance = 0;
    });
    this.input.on('pointermove', (pointer: Phaser.Input.Pointer) => {
      if (!pointer.isDown) return;
      const dx = pointer.x - pointer.prevPosition.x;
      const dy = pointer.y - pointer.prevPosition.y;
      this.dragDistance += Math.abs(dx) + Math.abs(dy);
      if (this.dragDistance < 6) return;
      const camera = this.cameras.main;
      camera.scrollX -= dx / camera.zoom;
      camera.scrollY -= dy / camera.zoom;
      this.movedByUser = true;
    });
    this.input.on('pointerup', (_pointer: Phaser.Input.Pointer, over: Phaser.GameObjects.GameObject[]) => {
      if (over.length === 0 && this.dragDistance < 6) this.onSelect(null);
    });
    this.input.on(
      'wheel',
      (_pointer: Phaser.Input.Pointer, _over: unknown, _dx: number, dy: number) => {
        const camera = this.cameras.main;
        camera.setZoom(Phaser.Math.Clamp(camera.zoom + (dy > 0 ? -0.5 : 0.5), 1, 6));
        this.movedByUser = true;
      },
    );
    this.scale.on('resize', () => {
      if (!this.movedByUser) this.fitCamera();
    });
  }
}
