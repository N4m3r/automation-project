# Permissões, Perfis e Grupos

Guia da camada de autorização da plataforma: como conceder acesso, como conferir
o que foi concedido e por que o modelo é assim.

---

## 1. O problema que este modelo resolve

Na modelagem anterior, um direito só podia apontar para **uma câmera específica**.
Configurar três grupos de usuário com quatro permissões sobre quarenta câmeras
exigia **480 linhas** de direito, criadas uma a uma. Pior: cada câmera nova
obrigava a repetir o trabalho, e esquecer uma passava despercebido — permissão
faltando não gera erro, gera um operador que simplesmente não vê a câmera.

O modelo atual separa duas perguntas que antes estavam misturadas:

| Pergunta | Responde | Onde vive |
|----------|----------|-----------|
| **O que** o usuário pode fazer? | Perfil (`Role`) | `/api/roles` |
| **Sobre quais** câmeras? | Direito (`ObjectRight`) | `/api/groups/{id}/access` |

Com as duas separadas, o mesmo cenário vira **3 linhas** — uma por grupo — e
câmera nova entra pelo grupo de câmera, sem tocar em permissão.

---

## 2. Conceitos

```
Usuário  ─┬─► Grupo de usuário ─┬─► Perfil ────────► permissões (o que pode fazer)
          │                     │
          │                     └─► Direitos ──────► grupos de câmera / câmeras
          │                                          (sobre o que pode agir)
          └─► Direitos individuais (exceção pontual)
```

### Perfil (`Role`)

Conjunto nomeado de permissões. Quatro vêm de fábrica e não podem ser editados
nem excluídos — para personalizar, duplique (`POST /api/roles/{id}/duplicate`):

| Perfil | Permissões |
|--------|-----------|
| **Visualizador** | `camera.view` |
| **Operador** | `camera.view`, `camera.playback`, `camera.ptz`, `event.ack` |
| **Supervisor** | as do Operador + `camera.export`, `audit.view` |
| **Administrador** | todas |

Alterar um perfil se reflete **imediatamente** em todos os grupos que o usam,
sem reescrever nenhuma linha de direito.

### Grupo de câmera (`CameraGroup`)

Agrupa câmeras e aceita hierarquia (`ParentId`). Um direito concedido sobre
"Prédio" alcança "Portaria" e "Garagem" abaixo dele.

A expansão acontece **na leitura**, não na gravação. Consequência prática: a
câmera adicionada ao grupo depois já nasce com o acesso — não há nada para
reconfigurar, e não existe estado desatualizado para corrigir.

### Direito (`ObjectRight`)

Liga um sujeito (usuário ou grupo) a um alvo:

| Campo | Valores | Significado |
|-------|---------|-------------|
| `objectType` | `camera` \| `cameragroup` | granularidade do alvo |
| `objectId` | id \| `null` | `null` = todos os objetos daquele tipo |
| `permission` | permissão \| `*` | `*` = "as permissões do perfil deste grupo" |
| `effect` | `Allow` \| `Deny` | Deny sempre vence |

O curinga `*` é o que faz a troca de perfil valer na hora: o direito guarda o
**alcance**, o perfil guarda as **permissões**, e nenhum dos dois precisa saber
do outro.

---

## 3. Regras de resolução

Nesta ordem, sem exceção:

1. **Administrador** (`User.IsAdmin`) ignora a checagem e vê tudo.
2. **Usuário inativo** não tem acesso a nada, independente dos direitos.
3. **Deny vence Allow**, sempre — inclusive Deny de câmera contra Allow de grupo.
4. `objectId` nulo vale para todos os objetos daquele tipo.
5. Direito sobre `cameragroup` expande para as câmeras do grupo **e dos subgrupos**.
6. Direitos de outro `TenantId` nunca são considerados.

Ciclo na árvore de grupos (`A → B → A`, possível em base legada) não trava a
resolução: a travessia marca os nós visitados e termina.

---

## 4. Configurando na prática

### Pela interface

**Gerenciamento de Usuários → Grupos e acesso → Configurar acesso.**
Uma janela responde às duas perguntas do modelo, com **prévia ao vivo** do
resultado enquanto se marca:

- **Perfil** — o que o grupo pode fazer (lista os perfis cadastrados);
- **Alcance** — todas as câmeras, ou grupos de câmeras e câmeras avulsas;
- **Exceções** — câmeras negadas (vence tudo);
- **Membros** — quem pertence ao grupo.

**Gerenciamento de Usuários → Perfis de permissão** cria e edita perfis.
**Direitos avançados** ficou para ajuste fino: concede direito individual,
permite alvo `cameragroup` e mostra os direitos efetivos com a origem de cada um.

### Pela API

### Cenário: grupo "Portaria" com acesso ao prédio, menos a câmera do cofre

**Uma chamada** configura perfil, alcance, exceções e membros, em transação:

```http
PUT /api/groups/5/access
Content-Type: application/json

{
  "roleId": 2,                 // Operador
  "cameraGroupIds": [1],       // grupo "Prédio" (inclui subgrupos)
  "deniedCameraIds": [17],     // exceto o cofre
  "userIds": [3, 8, 12]        // membros do grupo
}
```

Resposta devolve `camerasAlcancadas` — a lista efetiva, já resolvida.

Aplicar duas vezes não duplica nada: o alcance anterior é substituído por
inteiro. Sem `cameraGroupIds` nem `cameraIds`, o grupo recebe **todas** as
câmeras.

### Conferir antes de salvar

```http
POST /api/groups/preview
{ "roleId": 2, "cameraGroupIds": [1], "deniedCameraIds": [17] }
```

Retorna `{ total, cameras: [...], permissoes: [...] }`. Erro de configuração de
permissão é silencioso por natureza — a prévia é o que o torna visível.

### Entender um acesso já existente

```http
GET /api/rights/effective/{userId}
```

```jsonc
{
  "userId": 12, "username": "ana", "isAdmin": false, "active": true,
  "groups": ["Portaria"],
  "permissions": [
    { "permission": "camera.view",   "granted": true,
      "origin": "grupo \"Portaria\" (perfil) sobre grupo de camera \"Prédio\"" },
    { "permission": "camera.export", "granted": false,
      "origin": "Nenhum direito concede esta permissao" }
  ],
  "visibleCameras": [1, 2, 3]
}
```

O campo `origin` é o ponto: responde *por que* o acesso existe, não apenas se
existe. Sem ele, depurar permissão é tentativa e erro.

---

## 5. Referência de API

### Perfis — `/api/roles` (admin)

| Método | Rota | Observação |
|--------|------|-----------|
| `GET` | `/` | inclui `permissoes` e `gruposUsando` |
| `POST` | `/` | valida cada permissão |
| `PUT` | `/{id}` | recusa perfil de fábrica |
| `POST` | `/{id}/duplicate` | caminho para personalizar um perfil de fábrica |
| `DELETE` | `/{id}` | recusa se algum grupo usa; lista quais |

### Grupos de usuário — `/api/groups` (admin)

| Método | Rota | Observação |
|--------|------|-----------|
| `GET` | `/` | com perfil e contagem de membros |
| `GET` | `/{id}` | membros, acessos e `camerasAlcancadas` |
| `POST` | `/` | criação simples |
| `PUT` | `/{id}` | renomear / trocar perfil |
| `PUT` | `/{id}/access` | **configuração completa em transação** |
| `POST` | `/preview` | prévia sem gravar |
| `POST` | `/{id}/duplicate` | copia perfil e alcance, não os membros |
| `DELETE` | `/{id}` | remove membros e direitos em cascata |
| `POST`/`DELETE` | `/{groupId}/members/{userId}` | membro avulso |

### Direitos — `/api/rights` (admin)

| Método | Rota | Observação |
|--------|------|-----------|
| `GET` | `/permissions` | catálogo |
| `GET` | `/` | filtra por sujeito |
| `POST` | `/` | valida permissão, tipo, sujeito e objeto |
| `POST` | `/assign` | atribuição em lote |
| `GET` | `/effective/{userId}` | **com origem de cada direito** |
| `GET` | `/reach?subjectType=&subjectId=` | câmeras alcançadas por um sujeito |
| `DELETE` | `/{id}` | remove um direito |

### Grupos de câmera — `/api/admin/camera-groups` (admin)

| Método | Rota | Observação |
|--------|------|-----------|
| `GET` | `/` | árvore com membros |
| `POST` | `/` | criação |
| `PUT` | `/{id}` | renomear / mover; **recusa ciclo** |
| `GET` | `/{id}/impact` | quem perde acesso se excluir |
| `PUT` | `/{groupId}/cameras` | define os membros em lote |
| `DELETE` | `/{id}` | cascata; subgrupos sobem para a raiz |

---

## 6. Integridade referencial

Direito órfão — apontando para grupo, usuário ou câmera que não existe mais —
volta a valer se o id for reaproveitado. Por isso a exclusão limpa em cascata:

| Excluir | Também remove |
|---------|---------------|
| Usuário | associações de grupo + direitos individuais |
| Grupo de usuário | membros + direitos do grupo |
| Grupo de câmera | membros + direitos sobre o grupo; subgrupos sobem para a raiz |
| Câmera | gravações, arquivos, bookmarks, agendamentos, associações, direitos |

`POST /api/rights` também recusa, na entrada, direito que aponte para sujeito ou
objeto inexistente.

---

## 7. Cobertura de teste

`tests/SecurityPlatform.Tests/PermissionServiceTests.cs` — 12 testes sobre
SQLite em memória com o schema real:

- direito em grupo alcança as câmeras do grupo;
- direito no pai desce para os subgrupos;
- câmera adicionada depois já nasce com acesso;
- Deny pontual vence Allow amplo;
- trocar o perfil muda o acesso sem tocar nos direitos;
- permissão fora do perfil não é concedida;
- usuário inativo perde tudo;
- ciclo na árvore não trava a resolução;
- administrador vê tudo sem direito cadastrado;
- direitos de outro tenant não vazam;
- `ExplainAsync` informa a origem correta;
- a prévia bate exatamente com o acesso efetivo dos membros.

```bash
dotnet test tests/SecurityPlatform.Tests
```

---

## 8. O que ainda não está implementado

| Item | Situação | Impacto |
|------|----------|---------|
| Permissões globais escopadas por tenant | `user.manage`, `system.config` e `audit.view` ainda são gravadas com `objectType = "camera"` | funciona, mas o modelo é enganoso; um `objectType = "system"` deixaria mais claro |
| Filtro de tenant nos endpoints administrativos | apenas o `PermissionService` filtra por `TenantId` | numa instalação multi-cliente real, um admin de tenant enxerga entidades de outro; hoje a plataforma roda All-in-One com tenant 1 |
| Herança de direito entre grupos de usuário | não existe | grupo de usuário não herda de outro grupo de usuário (a hierarquia existe só do lado das câmeras) |
| Direitos com validade | não existe | não há acesso temporário com expiração automática |
| Herança entre grupos de usuário na UI | não se aplica | reflete a lacuna acima |
