# Sample1: ambiente de validação com Docker

Este guia descreve como gerar e usar a aplicação de amostra para validar o código local de JadByte.DurableMail: cadastro com ASP.NET Core Identity, enfileiramento persistente, envio SMTP, retries, consultas da fila e observação durante testes de carga.

O ponto de entrada é [`create_projects.bat`](../scripts/dotnet/windows/create_projects.bat). Ele cria todo o projeto em `samples/sample1`, aplica o template de [`sample1.zip`](../scripts/dotnet/sample1.zip) e publica a WebApi para Linux x64. **A criação e inicialização do ambiente Docker são uma etapa manual posterior**, com `docker compose up -d --build`.

O guia fica em `docs` para poder ser lido antes de gerar o sample e permanecer disponível quando ele for recriado. Os caminhos de configuração mencionados abaixo são relativos a `samples/sample1`, salvo indicação contrária. O ZIP é a fonte do template; alterações feitas na pasta gerada não atualizam o ZIP automaticamente.

Navegação: [primeira execução](#preparação-e-primeira-execução) · [serviços](#serviços-e-endereços) · [scripts e ZIP](#o-que-é-criado-pelos-scripts) · [pacotes GitHub](#pacotes-nuget-no-github-packages) · [Docker e atualização](#publicação-dockerfile-e-atualização-do-código) · [migrations](#migrations) · [VS Code](#vs-code) · [fila e retries](#dashboard-da-fila-e-retries) · [métricas](#prometheus-e-grafana) · [k6](#teste-de-carga-com-k6) · [diagnóstico](#situações-comuns).

## Preparação e primeira execução

### Pré-requisitos

| Ferramenta ou condição | Para que serve |
| --- | --- |
| Windows e `cmd.exe` | Executar o bootstrap `.bat` fornecido e os atalhos `RUN` do VS Code. |
| .NET SDK conforme [`global.json`](../global.json) | SDK `10.0.401`, com `latestPatch` e sem versões preview, para criar e publicar os projetos no host. |
| `tar` disponível no PATH | Extrair o template ZIP durante o bootstrap. |
| Docker com containers Linux e Compose | Construir e executar os sete serviços. `dockerfile_inline` exige Compose 2.17.0 ou superior, conforme a [referência do Docker](https://docs.docker.com/reference/compose-file/build/#dockerfile_inline). |
| Ambiente compatível com Linux x64 | O script de publicação usa explicitamente `-r linux-x64`; não há seleção automática de arquitetura nos scripts. |
| Acesso à internet | Restaurar NuGet/npm, baixar imagens e baixar o dashboard Grafana durante o build. O navegador também usa CDNs na interface do dashboard da fila. |
| Portas livres | `8080`, `3005`, `3306`, `8081`, `1025`, `8025`, `9090` e `3000`. |

Node.js/npm no host não são necessários para executar o dashboard em container. `k6` só é necessário para o teste de carga. `dotnet-ef` e o cliente `mysql` só são necessários para o fluxo opcional de gerenciamento de migrations descrito adiante; não são necessários para a aplicação inicial das migrations já fornecidas.

### 1. Gerar o sample

Abra o **Prompt de Comando do Windows na raiz do repositório** e execute:

```bat
call scripts\dotnet\windows\create_projects.bat
```

Esse é o único script necessário para a montagem inicial. Não é preciso chamar manualmente cada auxiliar. Ele resolve a raiz a partir de sua própria localização, cria os projetos e configura as referências, extrai o template e chama a publicação.

Ao terminar, confirme que a publicação foi concluída e que existe:

```text
samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/
  bin/Release/net10.0/linux-x64/publish/
    JadByte.DurableMail.Samples.Sample1.WebApi.dll
    JadByte.DurableMail.Samples.Sample1.Infrastructure.dll
```

O bootstrap atual não verifica todos os códigos de saída dos comandos internos; verifique o log e o artefato publicado, além do retorno do script. Se `samples/sample1/src` já existir, ele informa `Sample1 already exist.` e encerra: **não atualiza nem repara uma geração incompleta**. Preserve alterações locais e identifique a etapa que falhou antes de tentar recriar o ambiente.

### 2. Conferir a configuração local

O ZIP já inclui `.env`, `.env.example`, `secrets/db_password` e seu arquivo de exemplo. Revise os arquivos extraídos antes do primeiro startup; não é necessário sobrescrevê-los com os exemplos quando já existem.

| Configuração | Origem e uso |
| --- | --- |
| `MARIADB_HOST` | `.env`; valor do template `mariadb`, nome DNS do serviço dentro do Compose. |
| `MARIADB_NAME` | `.env`; banco da aplicação, inicialmente `jad_byte_durable_mail_sample1`. |
| `MARIADB_USER` | `.env`; usuário da aplicação, inicialmente `jadByteSample1`. |
| Senha do banco | `secrets/db_password`; secret `db_password`, montado em `/run/secrets/db_password`. Não fica em `MARIADB_PASSWORD` no `.env`. |
| `SMTP_HOST` / `SMTP_PORT` | `.env`; `mailpit` e `1025` para comunicação dentro da rede Docker. |
| `SMTP_FROMNAME` / `SMTP_FROMEMAIL` | `.env`; remetente do sample. |
| `SMTP_USERNAME` / `SMTP_PASSWORD` | Vazios no template; o SMTP local não exige autenticação. |
| `SMTP_SECURITYMODE` | `4`, correspondente a `None` no transporte MailKit da biblioteca, para esse SMTP local sem TLS. |
| `SMTP_USESTARTTLS` | O template também passa essa variável para a seção `Smtp`; a seleção de segurança do transporte da biblioteca é feita por `SecurityMode`. |
| `LUCKYPENNY_LICENSE_KEY` | Campo vazio do template repassado como `LicenseKeys__LuckyPenny`; não é uma credencial do MariaDB ou do Mailpit. |

Na WebApi, o entrypoint do Dockerfile lê o secret e monta `ConnectionStrings__DefaultConnection` com `Server`, `Port=3306`, `Database`, `User Id`, `Password` e `TreatTinyAsBoolean=false`. Ele também grava exports em `/root/.bashrc` dentro do container. No MariaDB, `MARIADB_PASSWORD_FILE` aponta para o mesmo secret. No dashboard, a cópia de `server.js` na imagem lê esse arquivo diretamente.

Dentro dos containers, use `mariadb:3306` e `mailpit:1025`. Ferramentas executadas no Windows usam as portas publicadas, por exemplo `localhost:3306` e `localhost:1025`. O `.env` é usado pelo Compose; ele não configura automaticamente uma WebApi iniciada diretamente pelo debugger .NET.

`nuget.config.example` é apenas um exemplo de configuração de feeds, com placeholders. Ele não é ativado pelo bootstrap; não é necessário fornecer um token de GitHub para referenciar as bibliotecas locais. O uso opcional de pacotes publicados está descrito em [Pacotes NuGet no GitHub Packages](#pacotes-nuget-no-github-packages).

### 3. Iniciar os containers

Depois da geração, entre na pasta do sample e execute:

```bat
cd samples\sample1
docker compose config --quiet
docker compose up -d --build
docker compose ps
docker compose logs --tail 100 webapi
```

`create_projects.bat` não executa esse passo. A WebApi, o dashboard e o phpMyAdmin aguardam o healthcheck do MariaDB. A aplicação aplica as migrations pendentes antes de começar a atender HTTP; se isso falhar, o erro aparece nos logs da WebApi. O healthcheck do banco não representa, por si só, a conclusão das migrations da aplicação: aguarde a API ficar pronta antes de iniciar a carga.

## Serviços e endereços

| Serviço Compose | Acesso no host | Responsabilidade |
| --- | --- | --- |
| `webapi` | `http://localhost:8080/` | API do sample e interface Scalar em Development; `/metrics` expõe as métricas. |
| `durable-mail-dashboard` | `http://localhost:3005/` | Consulta a fila no MariaDB e apresenta status, tentativas e erros. |
| `mariadb` | `localhost:3306` | Banco da aplicação, tabelas Identity e persistência DurableMail; não é uma página HTTP. |
| `phpmyadmin` | `http://localhost:8081/` | Inspeção web do MariaDB; o template conecta como `root`, sem senha. |
| `mailpit` | `http://localhost:8025/`; SMTP em `localhost:1025` | Recebe e permite inspecionar os e-mails de teste; injeta falhas com Chaos. |
| `prometheus` | `http://localhost:9090/` | Coleta métricas da WebApi e mantém séries temporais. |
| `grafana` | `http://localhost:3000/` | Visualiza as séries do Prometheus; numa instalação nova, login `admin` / `admin`. |

Os nomes de container são fixos: `jadByteSample1` para a API e `jadByteSample1-<serviço>` para os demais, incluindo `jadByteSample1-durable-mail-dashboard`. Instâncias simultâneas desse mesmo Compose podem conflitar por nome, porta e pelo nome explícito do volume do MariaDB.

O conjunto foi configurado para desenvolvimento local: API em Development, interfaces sem autenticação em alguns serviços, root sem senha no banco e credenciais de demonstração. As portas publicadas não são limitadas a `127.0.0.1` no YAML. Use dados de teste; as interfaces do Mailpit, phpMyAdmin e dashboard podem revelar destinatários, conteúdo e links de confirmação/reset.

## O que é criado pelos scripts

### Projetos e implementação

O bootstrap gera três class libraries e uma WebApi, remove os `Class1.cs` dos templates e adiciona os quatro projetos à solução raiz `JadByte.DurableMail.slnx`.

| Projeto | Referências e função no sample |
| --- | --- |
| `JadByte.DurableMail.Samples.Sample1.Domain` | Camada base criada pelo template. |
| `JadByte.DurableMail.Samples.Sample1.Application` | Referencia Domain; recebe os pacotes de aplicação. |
| `JadByte.DurableMail.Samples.Sample1.Infrastructure` | Referencia Domain, Application e os projetos locais `JadByte.DurableMail.EntityFrameworkCore` e `JadByte.DurableMail.MailKit`; implementa persistência e envio Identity. |
| `JadByte.DurableMail.Samples.Sample1.WebApi` | Referencia Application e Infrastructure; configura endpoints, migrations e métricas. |

As referências locais alcançam o Core pelas dependências dos adapters. O fluxo permite validar mudanças no código da biblioteca sem publicar seus pacotes no NuGet. Ter o projeto ASP.NET Core da biblioteca no contexto Docker não significa que o middleware opcional de retry esteja habilitado: o sample utiliza o trigger agendado.

### Catálogo dos scripts

Todos os caminhos desta tabela são relativos à raiz do repositório.

| Arquivo | Responsabilidade |
| --- | --- |
| [`scripts/dotnet/windows/create_projects.bat`](../scripts/dotnet/windows/create_projects.bat) | Orquestra criação, configuração, extração do ZIP e publicação. As chamadas de adicionar projetos à solução e de referências aparecem duas vezes no fluxo atual. Não inicia Docker; a chamada de reset de migrations está comentada. |
| [`scripts/dotnet/windows/inc/remove_version.bat`](../scripts/dotnet/windows/inc/remove_version.bat) | Remove a versão explícita de `Microsoft.AspNetCore.OpenApi` no `.csproj` gerado quando encontra o literal `10.0.12`, para usar a versão centralizada. Não é uma remoção genérica de todas as versões. |
| [`scripts/dotnet/windows/inc/create_props.bat`](../scripts/dotnet/windows/inc/create_props.bat) | Cria/sobrescreve `Directory.Packages.props` do sample com Central Package Management e as versões dos pacotes. |
| [`scripts/dotnet/windows/adding_projects_to_sln.bat`](../scripts/dotnet/windows/adding_projects_to_sln.bat) | Insere os quatro projetos na solução, verificando se seus caminhos já estão presentes. |
| [`scripts/dotnet/windows/referencing_projects.bat`](../scripts/dotnet/windows/referencing_projects.bat) | Configura os `ProjectReference` e chama o auxiliar de framework reference. |
| [`scripts/dotnet/windows/inc/add_framework_reference.bat`](../scripts/dotnet/windows/inc/add_framework_reference.bat) | Adiciona `FrameworkReference` de `Microsoft.AspNetCore.App` a Infrastructure; verifica referência existente e rejeita o pacote legado com esse nome. |
| [`scripts/dotnet/windows/add_package_to_project.bat`](../scripts/dotnet/windows/add_package_to_project.bat) | Adiciona pacotes de aplicação, Identity/Pomelo/MailKit e OpenAPI/OpenTelemetry às respectivas camadas. |
| [`scripts/dotnet/windows/buildForDocker.bat`](../scripts/dotnet/windows/buildForDocker.bat) | Executa no Windows as linhas iniciadas por `dotnet publish ` de `linux/publish.sh`. Publica no host; não chama Docker, Bash ou migrations. |
| [`scripts/dotnet/linux/publish.sh`](../scripts/dotnet/linux/publish.sh) | Publica a WebApi em `Release` para `linux-x64`. Deve ser executado da raiz; não monta o projeto nem equivale ao bootstrap Windows. |
| [`scripts/dotnet/windows/generate_migration.bat`](../scripts/dotnet/windows/generate_migration.bat) | Apaga migrations e banco, cria uma nova migration e aplica-a. Uso restrito a reset deliberado de dados descartáveis. |
| [`scripts/k6/loadtest.js`](../scripts/k6/loadtest.js) | Define a carga HTTP de cadastro. |
| [`scripts/k6/run.bat`](../scripts/k6/run.bat) | Recria o Prometheus apagando seu histórico e executa o k6. |

Os principais grupos de pacotes são AutoMapper/FluentValidation/MediatR em Application; Pomelo, Identity EF Core, MailKit e convenções snake_case em Infrastructure; Scalar, EF Design e OpenTelemetry na WebApi. A fonte das versões é `Directory.Packages.props`, gerada pelo auxiliar e também fornecida pelo ZIP. O sample é `net10.0`, mas usa EF Core/Identity/Design `9.0.20` e Pomelo `9.0.0`; o target framework não determina sozinho a versão do EF.

### Conteúdo do ZIP

O ZIP tem 47 entradas, incluindo diretórios. Ele contém configurações Docker/ambiente, arquivos de VS Code, o dashboard, secrets de desenvolvimento, implementação de Infrastructure/WebApi e a migration inicial com designer e snapshot. Seu `README.md` está vazio no template atual.

Não contém os `.csproj`, nem a implementação dos templates Domain/Application. Por isso **extrair apenas o ZIP não substitui `create_projects.bat`**. O script gera os projetos primeiro e extrai o ZIP depois, sobrepondo os arquivos de implementação/configuração fornecidos.

O `.gitignore` interno ignora, entre outros, `.env`, secrets locais, `appsettings.*json` e pastas `Migrations`. A presença da migration dentro do ZIP não significa que alterações futuras na pasta extraída serão automaticamente versionadas. Revise isso quando decidir preservar a evolução do sample; este guia não altera essas regras nem o ZIP.

## Pacotes NuGet no GitHub Packages

Além do código-fonte, o autor informa que já publicou pacotes compilados no GitHub Packages e apresentou `0.1.0` como exemplo de consumo. Esse é um modo alternativo ao `ProjectReference`: valida o artefato distribuído, não as mudanças ainda não empacotadas no código local. O bootstrap continua usando os projetos locais; não troca para pacotes automaticamente.

### Fonte e autenticação

O `nuget.config.example` do ZIP mantém o nuget.org para as demais dependências e adiciona a fonte `MyGitHub`:

```xml
<packageSources>
  <clear />
  <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  <add key="MyGitHub" value="https://nuget.pkg.github.com/Jadiael1/index.json" />
</packageSources>
```

O endereço é o feed NuGet da conta `Jadiael1`, **não `https://github.com`**. Para usar a configuração, copie o exemplo para `samples/sample1/nuget.config` e configure a autenticação do consumidor; o sufixo `.example` impede seu uso automático. O exemplo também define `globalPackagesFolder`, uma pasta de cache relativa à configuração. Manter os dois feeds disponíveis não estabelece uma ordem de fallback entre eles.

Para acesso local ao registry, use um personal access token **classic**; pacotes públicos nesse registry também exigem autenticação. O usuário de autenticação é o dono do token, não necessariamente `Jadiael1`, que identifica a conta proprietária do feed. Veja a [documentação do registry NuGet do GitHub](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry). Para consumir, o token precisa de `read:packages` e a conta precisa ter acesso; publicar exige `write:packages` e permissão correspondente. Consulte as [permissões do GitHub Packages](https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages).

O `ClearTextPassword` do exemplo contém apenas a máscara `ghp_***...`, não um token utilizável. Os `.gitignore` conferidos **não ignoram `nuget.config`**: não grave nem versione um token real nesse arquivo. Uma opção é substituir os valores das credenciais por referências a variáveis de ambiente:

```xml
<packageSourceCredentials>
  <MyGitHub>
    <add key="Username" value="%GITHUB_PACKAGES_USERNAME%" />
    <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
  </MyGitHub>
</packageSourceCredentials>
```

Defina essas variáveis no ambiente do processo de restore usando seu mecanismo local de segredos; não coloque seus valores em scripts versionados. A sintaxe `%VARIAVEL%` é interpretada pelo NuGet nesse XML. Veja [credenciais para feeds autenticados](https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds). Isso não configura o build Docker automaticamente.

### Referências e compatibilidade de versões

**Confira primeiro as dependências do pacote que será consumido.** Na inspeção local, `artifacts/packages/JadByte.DurableMail.EntityFrameworkCore.0.1.0.nupkg` declara EF Core/Relational `10.0.12`, mas o sample usa Pomelo `9.0.0` e Identity/Design `9.0.20`. O `.nuspec` de Pomelo no cache local aceita Relational de `9.0.0` a `9.0.999`: esse artefato local `0.1.0` é incompatível com o provider do template. A fonte atual da biblioteca já usa EF Core `9.0.20`; um `.nupkg` antigo não acompanha alterações do código-fonte.

O conteúdo do pacote remoto não foi comparado ao artefato local. Portanto, os exemplos `0.1.0` abaixo demonstram a configuração, **não comprovam compatibilidade dessa versão publicada com o sample**. Antes da troca, selecione uma versão publicada cujas dependências estejam alinhadas com o provider e com os pacotes EF/Identity/Design da aplicação. Não altere apenas a versão do EF ignorando a faixa suportada pelo Pomelo.

Em um projeto consumidor **sem Central Package Management**, as referências em Infrastructure podem declarar a versão diretamente:

```xml
<ItemGroup>
  <PackageReference Include="JadByte.DurableMail.EntityFrameworkCore" Version="0.1.0" />
  <PackageReference Include="JadByte.DurableMail.MailKit" Version="0.1.0" />
</ItemGroup>
```

No sample gerado, o gerenciamento central está habilitado. Nesse caso, acrescente as versões ao `samples/sample1/Directory.Packages.props`, no `ItemGroup` existente:

```xml
<PackageVersion Include="JadByte.DurableMail.EntityFrameworkCore" Version="0.1.0" />
<PackageVersion Include="JadByte.DurableMail.MailKit" Version="0.1.0" />
```

E use no `.csproj` de Infrastructure:

```xml
<ItemGroup>
  <PackageReference Include="JadByte.DurableMail.EntityFrameworkCore" />
  <PackageReference Include="JadByte.DurableMail.MailKit" />
</ItemGroup>
```

Remova **somente** os dois `ProjectReference` para os adapters locais correspondentes; mantenha as referências Domain/Application, o `FrameworkReference` e os demais pacotes. O Core é uma dependência transitiva dos adapters e também precisa estar disponível no feed. O sample não exige o pacote opcional `JadByte.DurableMail.AspNetCore`. A separação entre `PackageVersion` e `PackageReference` segue o [Central Package Management](https://learn.microsoft.com/pt-br/nuget/consume-packages/central-package-management); duplicar `Version` no `.csproj` nesse modo causa `NU1008`.

Depois de alinhar versões e configurar credenciais, um restore explícito, **da raiz do repositório**, é:

```bat
dotnet restore samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/JadByte.DurableMail.Samples.Sample1.WebApi.csproj --configfile samples/sample1/nuget.config
```

Reexecutar `referencing_projects.bat` reintroduz as referências locais. `create_props.bat` ou uma nova extração do ZIP também podem sobrescrever personalizações. Essa conversão é uma configuração opcional do consumidor, não uma etapa necessária para testar o código deste repositório.

### Docker e publicação dos pacotes

O Dockerfile fornecido **não copia `nuget.config` antes do restore nem fornece credenciais do GitHub ao build**. Ter restore autenticado funcionando no Windows não torna o restore dentro da imagem autenticado. As variáveis `environment` do Compose e o secret MariaDB são de runtime, não resolvem esse acesso. Para adotar pacotes nesse build, será preciso configurar a fonte e disponibilizar a credencial de forma temporária na etapa de restore, sem incorporá-la à imagem. Essa adaptação não está implementada no template atual. O bind mount `/app` continua exigindo a publicação atualizada no host.

Para o **mantenedor** publicar um `.nupkg` já gerado e revisado, o formato do comando no Prompt de Comando é:

```bat
dotnet nuget push "CAMINHO_DO_PACOTE.nupkg" --source "https://nuget.pkg.github.com/Jadiael1/index.json" --api-key "%GITHUB_PACKAGES_TOKEN%"
```

Substitua o caminho pelo artefato e use um token com autorização de publicação, já configurado no ambiente; não compartilhe logs que o exponham. Esse comando altera o registry remoto, não é necessário para o consumidor e não é executado pelo bootstrap. Os comandos de `pack` do [README](../README.md#build-and-run) geram os pacotes em `artifacts/packages`; gerar um pacote e enviá-lo ao GitHub são etapas diferentes. O formato do feed e do push está documentado no [registry NuGet do GitHub](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

## Publicação, Dockerfile e atualização do código

O build da WebApi usa `context: ../..`, a raiz do repositório, e `dockerfile: samples/sample1/Dockerfile`. Assim o Dockerfile consegue copiar os projetos da biblioteca e do sample. `Dockerfile.dockerignore` exclui `bin`, `obj`, `.git`, `.vs` e `.vscode` desse contexto.

O Dockerfile tem dois estágios: SDK .NET 10 para restore/publish e ASP.NET Runtime 10 para executar a DLL publicada. As migrations entram compiladas no assembly Infrastructure; o runtime não precisa dos arquivos `.cs` nem de `dotnet-ef` para aplicá-las.

O `Directory.Build.props` vazio do sample também é copiado antes do restore. Por ser o arquivo mais próximo dos projetos, ele delimita as configurações do sample e não importa automaticamente as regras da raiz, como `TreatWarningsAsErrors=true` e `AnalysisLevel=latest-recommended`. As bibliotecas em `src/` continuam usando as regras da raiz. Isso decorre da [busca do MSBuild por configurações de diretório](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory).

Há uma particularidade central no Compose atual:

```yaml
volumes:
  - type: bind
    source: ./src/JadByte.DurableMail.Samples.Sample1.WebApi/bin/Release/net10.0/linux-x64/publish/
    target: /app
```

Essa montagem faz o container usar os binários publicados no host. O conteúdo da imagem que já existia em `/app` fica encoberto, conforme a [semântica de bind mounts](https://docs.docker.com/engine/storage/bind-mounts/#bind-mounting-over-existing-data). É por isso que a publicação ao fim de `create_projects.bat` é necessária, mesmo que o Dockerfile também execute `dotnet publish`.

Para atualizar código da WebApi ou da biblioteca, em um ambiente já criado, execute em `samples/sample1`:

```bat
call ..\..\scripts\dotnet\windows\buildForDocker.bat
```

Depois de confirmar o sucesso da publicação:

```bat
docker compose restart webapi
```

O atalho VS Code `RUN - RebuildForDocker` faz a publicação e em seguida executa `docker compose restart` sem indicar serviço, reiniciando todos os serviços. Um `docker compose up -d --build` isolado reconstrói imagens, mas não atualiza a pasta de publicação do host. Mudanças no Compose ou nas variáveis de ambiente exigem recriação pelo `up`; `restart` apenas reinicia os containers existentes.

O dashboard tem outro fluxo: `server.js` e `public/` são copiados para sua imagem, sem bind mount do código. Depois de alterar esses arquivos, reconstrua o serviço com `docker compose up -d --build durable-mail-dashboard`.

Para um sample já gerado, a publicação pode ser feita em Linux com `bash scripts/dotnet/linux/publish.sh`, a partir da raiz do repositório. Esse script não oferece um bootstrap Linux equivalente aos `.bat`.

## Fluxo funcional da aplicação

```text
POST /v1/signUp
  -> ASP.NET Core Identity cria o usuário
  -> IEmailSender<ApplicationUser> solicita confirmação
  -> IEmailQueue persiste a entrega no MariaDB
  -> dispatcher envia por MailKit para mailpit:1025
  -> sucesso ou falha/tentativa/retry são persistidos
```

O grupo de endpoints Identity está em `/v1`. O template renomeia cadastro para `/v1/signUp` e login para `/v1/signIn`; os demais endpoints Identity permanecem no grupo. O registro exige e-mail único e o sign-in exige confirmação.

O adapter `EmailSender` transforma confirmação e recuperação de senha em mensagens HTML, com `Source` explícito: `identity:confirmation`, `identity:password-reset` ou `identity:password-reset-code`. Ele chama `IEmailQueue.EnqueueAsync`; não fornece `DeduplicationKey` nesse fluxo.

Para uma verificação manual, abra Scalar em `http://localhost:8080/` e envie um `POST /v1/signUp` com um e-mail ainda não cadastrado:

```json
{
  "email": "sample1-primeiro-teste@example.com",
  "password": "Password123!"
}
```

Confira separadamente a resposta HTTP, a entrega no dashboard e a chegada da mensagem no Mailpit. Se o envio tiver sucesso, o e-mail de confirmação poderá ser aberto em `http://localhost:8025/`. O código aguarda a persistência do enqueue, não a conclusão do SMTP; HTTP 200 não garante que o e-mail já chegou.

Identity e enqueue não compõem uma única transação de negócio. A biblioteca tem persistência independente e entrega **at least once**: uma interrupção depois da aceitação SMTP e antes do registro de sucesso pode resultar em duplicata. O sample permite observar a integração, mas a carga de cadastro não comprova deduplicação, exactly-once ou todos os cenários de resiliência. As garantias e os limites estão descritos no [README da biblioteca](../README.md#concurrency-leases-and-delivery-guarantees).

## Migrations

O template já contém `20260929170532_Migration20260929140514`, seu designer e `ApplicationDbContextModelSnapshot.cs`. A migration cria tabelas Identity e `durable_mail_deliveries`, `durable_mail_attempts` e `durable_mail_slots`.

Na inicialização da WebApi, `Program.cs` abre um escopo, resolve `ApplicationDbContext` e executa `await context.Database.MigrateAsync()`. O banco precisa estar acessível; a configuração usa `ServerVersion.AutoDetect`. Só depois a aplicação chega a `await app.RunAsync()`.

O EF usa `__ef_migrations_history` para saber o que já foi aplicado. O bloco roda a cada inicialização: aplica todas as migrations compiladas pendentes num banco novo, não repete as registradas e aplica novas migrations quando publicadas posteriormente. Ele não gera arquivos de migration. A primeira execução normal não precisa do atalho `GenerateMigration`.

### O atalho GenerateMigration é um reset

**Esse script apaga dados e o histórico de migrations.** Seu comportamento atual é:

1. Ler `MARIADB_NAME` e `MARIADB_USER` do `.env` e a primeira linha de `secrets/db_password`.
2. Definir a conexão EF para `localhost:3306`, pois o comando roda no host.
3. Apagar a pasta `Infrastructure/Migrations`, incluindo o snapshot.
4. Executar `mysql -u root -e "DROP DATABASE IF EXISTS ..."`.
5. Gerar uma nova migration e executar `dotnet ef database update`, usando Infrastructure como projeto e WebApi como startup project.

Ele requer `mysql` no PATH, `dotnet-ef` compatível com o EF Core 9.0.20 e o MariaDB disponível. A invocação de `mysql` não recebe host, porta nem senha explicitamente: usa as configurações do cliente e **não utiliza** `ConnectionStrings__DefaultConnection`. Confirme que o alvo administrativo é o banco descartável pretendido antes de executar. O nome da migration também depende do formato regional de `%DATE%` e `%TIME%`.

Para esse reset deliberado, o comando em `samples/sample1` é:

```bat
call ..\..\scripts\dotnet\windows\generate_migration.bat
```

O script muda o diretório corrente para a raiz do repositório e não o restaura; no mesmo Prompt de Comando, volte a `samples/sample1` antes de executar comandos Compose. Após uma alteração de migrations, republique e reinicie a WebApi para que `/app` receba a mesma versão dos assemblies. Para evolução incremental preservando os dados, use o fluxo normal do EF (`migrations add` e `database update`) com a conexão do host configurada e os projetos corretos; não use esse script de reset como equivalente a uma atualização incremental.

## VS Code

Abra **`samples/sample1` como pasta do workspace**, por exemplo com `code .` nesse diretório. Os caminhos `${workspaceFolder}` dos perfis assumem essa raiz. `.vscode/settings.json` aponta a solução para `../../JadByte.DurableMail.slnx`, e `extensions.json` recomenda C# e .NET Runtime.

Em Run and Debug, o template oferece:

| Perfil | Ação e efeito |
| --- | --- |
| `DEV - JadByteDurableMailSample1.WebApi` | Executa a WebApi Debug no host, após a task `build`, e abre o endereço anunciado pelo servidor. Configura apenas `ASPNETCORE_ENVIRONMENT=Development`. |
| `RUN - RebuildForDocker` | Chama `buildForDocker.bat` e depois reinicia todos os serviços do Compose. |
| `RUN - GenerateMigration` | Executa o reset de migrations/banco descrito acima. |
| `RUN - k6 run` | Chama o wrapper que apaga o histórico do Prometheus e inicia a carga HTTP. |

Os perfis `RUN` invocam `cmd.exe` e são voltados a Windows. O perfil `DEV` não passa pelo entrypoint Docker, não lê automaticamente o secret e não recebe o ambiente do Compose. Antes de usá-lo, configure `ConnectionStrings__DefaultConnection` e `Smtp` para acesso pelo host, com `localhost` nas portas publicadas; evite iniciar duas APIs disputando a mesma porta.

`tasks.json` fornece `build`, `publish` e `watch` para a WebApi. A task `publish` comum não inclui automaticamente `-c Release -r linux-x64`, portanto não substitui `RebuildForDocker`. O arquivo `keybindings.json` contém preferências de atalhos; sua presença na pasta `.vscode` não equivale à instalação automática das keybindings pessoais do editor.

## Dashboard da fila e retries

O dashboard usa Node 22 Alpine, Express e o conector MariaDB. O `dockerfile_inline` instala dependências com `--omit=dev --ignore-scripts`, copia o código e adapta a cópia de `server.js` com `sed` para ler `DB_HOST`, `DB_PORT`, `DB_USER`, `DB_NAME` e `DB_PASSWORD_FILE`. O original no ZIP continua com placeholders locais. Essa adaptação depende dos trechos atuais do servidor e precisa ser revista se eles mudarem.

O serviço limita CPU a `0.25`, memória a `128m`, old space do heap V8 a 64 MiB, pool a duas conexões e PIDs/tarefas a 64. Usa usuário `node`, filesystem somente leitura, `/tmp` em tmpfs de 16 MiB, `no-new-privileges` e `cap_drop: ALL`. Esses são limites configurados, não medições de consumo nem limites do ambiente inteiro.

O `package.json` ainda lista `sqlite3`, embora o servidor use MariaDB. No template atual não há remoção dessa dependência no Dockerfile nem `package-lock.json` no ZIP; `npm install` resolve as faixas declaradas. Não assuma builds idênticos de dependências a partir de datas diferentes.

| Visão/API | O que mostra |
| --- | --- |
| Cards e gráfico; `/api/stats` | Total e contagens por estado das entregas. A API também retorna `avg_attempts` de entregas bem-sucedidas, mas a UI atual não mostra essa média em um card. |
| Recent Failures; `/api/recent-failures` | Últimas dez tentativas com falha transitória ou permanente, com detalhes de erro e stack trace expansíveis. |
| Entregas recentes; `/api/deliveries` | Últimas vinte entregas, estado e quantidade de tentativas. |

A página atualiza ao abrir e ao clicar em **Refresh Data**; não há atualização automática periódica. Tailwind e Chart.js são carregados por CDN. O dashboard consulta dados; ele não dispara o processamento nem oferece reenvio manual.

Os estados são `Pending`, `Processing`, `Succeeded`, `Failed` e `Superseded`. `Pending` inclui tanto trabalho elegível quanto espera pelo `next_attempt_at`. `Processing = 0` pode ocorrer entre envios rápidos. Recent Failures contém o histórico de tentativas e pode mostrar erros de uma entrega que depois teve sucesso, mesmo com `Failed = 0`.

### Mailpit Chaos e tempo de espera

O Compose habilita:

```yaml
MP_ENABLE_CHAOS: "true"
MP_CHAOS_TRIGGERS: Sender:451:10,Recipient:451:10,Authentication:535:10
```

O último campo de cada gatilho configura probabilidade de 10% de falha naquela etapa SMTP, conforme a [documentação Chaos do Mailpit](https://mailpit.axllent.org/docs/integration/chaos/); não é uma taxa global de 10% dos e-mails. `451` é classificado como transitório pelo adapter MailKit; erros `5xx` são permanentes. O gatilho de autenticação só se aplica quando essa etapa ocorre; o template usa credenciais SMTP vazias.

O sample ativa retries agendados e mantém os demais defaults da biblioteca: cinco tentativas totais, incluindo a primeira, atraso inicial de dois minutos, fator dois e jitter de ±15%. Após falhas consecutivas, os intervalos-base entre tentativas são 2, 4, 8 e 16 minutos. Falha permanente ou esgotamento do limite torna a entrega `Failed`.

O dispatch também mantém os defaults: lote de até 25, grau máximo de paralelismo 1 por dispatcher e lease de dois minutos. Cinquenta usuários virtuais no k6 não configuram cinquenta envios SMTP simultâneos.

O dispatcher usa um timer one-shot para o próximo evento persistido e reidrata o agendamento ao iniciar. Não é necessário manter o dashboard aberto nem chamar a API para acordar esses retries. Por causa dos atrasos, o fim de uma carga de cerca de 100 segundos não implica fila vazia. As entregas e tentativas podem ser examinadas no phpMyAdmin; timestamps DurableMail são ticks UTC de .NET armazenados em `BIGINT`, não Unix milliseconds.

`MP_DATABASE=/data/mailpit.db` persiste os e-mails no volume Mailpit, e `MP_MAX_MESSAGES=0` desativa o limite automático por quantidade. Rodadas sucessivas acumulam dados nesse ambiente.

## Prometheus e Grafana

A WebApi instrumenta ASP.NET Core e o runtime .NET e disponibiliza `/metrics`. O Prometheus usa o job `jadByteSample1-api`, alvo interno `webapi:8080` e intervalo de coleta de cinco segundos; o intervalo global é quinze segundos.

O Grafana provisiona o datasource Prometheus em `http://prometheus:9090`, com UID `prometheus_ds`. Durante o build, baixa o dashboard `19924`, revisão `5`, e substitui sua referência de datasource. A disponibilidade desse download e a compatibilidade das consultas com as métricas exportadas afetam os painéis.

Use `http://localhost:9090/targets` para verificar o alvo e `http://localhost:8080/metrics` para inspecionar a exposição. No Grafana, selecione um intervalo de tempo que inclua a execução do teste. Os cards de fila do dashboard Node vêm diretamente do MariaDB, enquanto Prometheus/Grafana observam HTTP e runtime; a configuração não fornece monitoramento completo de todos os containers, nem envia automaticamente os resultados do k6 ao Prometheus.

## Teste de carga com k6

O script [`loadtest.js`](../scripts/k6/loadtest.js) faz `POST http://localhost:8080/v1/signUp` com JSON, verifica HTTP 200 e aguarda um segundo entre iterações de cada usuário virtual.

| Etapa | Duração | Usuários virtuais alvo |
| --- | --- | --- |
| Subida | 30 segundos | 50 |
| Sustentação | 1 minuto | 50 |
| Descida | 10 segundos | 0 |

A duração nominal das etapas é 1 minuto e 40 segundos. Cinquenta usuários virtuais não equivalem a cinquenta requisições por segundo: cada um aguarda a resposta HTTP e o `sleep(1)`. Não há quantidade fixa de cadastros.

Com o binário `k6` no PATH e a API pronta, o wrapper pode ser chamado **da raiz do repositório**:

```bat
call scripts\k6\run.bat
```

Antes da carga, esse wrapper para e remove o container Prometheus, **remove o volume `sample1_prometheus_data`** e sobe o serviço novamente. Isso apaga séries históricas; não apaga MariaDB, usuários, fila, mensagens do Mailpit nem o volume Grafana. O nome do volume pressupõe o projeto Compose padrão `sample1`. Não há checagem explícita de falhas desses passos nem espera pela prontidão da WebApi no wrapper.

Para executar a mesma carga preservando o histórico Prometheus, use diretamente, também da raiz:

```bat
k6 run scripts\k6\loadtest.js
```

Os e-mails seguem `teste_${__VU}_${__ITER}@example.com`, com senha fixa de teste. Esse padrão se repete quando o k6 é executado novamente. Como o Identity exige e-mail único, rodadas posteriores sobre o mesmo banco podem rejeitar cadastros já existentes. Apagar métricas do Prometheus não resolve essas duplicidades. Para comparar rodadas, planeje dados de teste distintos ou um reset deliberado de uma base descartável.

O único check é HTTP 200. Não há `thresholds`, critério automatizado de latência nem verificação de entrega SMTP. Checks falhos, sozinhos, não definem uma condição de falha do processo k6; critérios de aprovação exigem thresholds, conforme a [documentação do k6](https://grafana.com/docs/k6/latest/using-k6/checks/). Os resultados nativos do k6 ficam no terminal; acompanhe separadamente a fila, o Mailpit, logs e gráficos HTTP/runtime.

## Persistência e comandos de consulta

| Volume Compose | Dados |
| --- | --- |
| `mariadb_data_jad_byte_durable_mail_sample1` | Banco, usuários Identity, entregas, tentativas e histórico de migrations. Nome Docker explícito: `jad_byte_durable_mail_sample1_data`. |
| `mailpit-data` | Mensagens do Mailpit em `/data/mailpit.db`. |
| `prometheus_data` | Séries temporais; normalmente `sample1_prometheus_data`. |
| `grafana_data` | Estado e configurações persistentes do Grafana. |

Os volumes sem nome explícito recebem o prefixo do projeto Compose. `docker compose stop` para os serviços; `docker compose down` remove containers/rede e preserva os volumes nomeados. A opção `down -v` também remove esses volumes e seus dados. Recriar apenas os containers não corresponde a um banco vazio nem reaplica migrations já registradas.

As variáveis de inicialização do MariaDB não reconfiguram automaticamente as contas de um banco já existente no volume. Alterar apenas `.env` ou `secrets/db_password` não troca sua senha persistida; consulte o [comportamento da imagem MariaDB](https://mariadb.com/docs/server/server-management/automated-mariadb-deployment-and-administration/docker-and-mariadb/mariadb-server-docker-official-image-environment-variables) antes de alterar credenciais de um ambiente com dados.

Consultas úteis, executadas em `samples/sample1`:

```bat
docker compose ps
docker compose logs --tail 100 webapi durable-mail-dashboard mariadb mailpit
docker compose logs -f webapi mailpit
docker stats --no-stream
curl.exe http://localhost:3005/api/stats
curl.exe http://localhost:8080/metrics
```

## Situações comuns

| Sintoma | O que conferir |
| --- | --- |
| Bootstrap diz que o sample já existe | A condição é a existência de `samples/sample1/src`; não há reparo automático. Examine os passos anteriores e preserve seus arquivos. |
| Erro de versão explícita em pacote centralizado | O helper remove especificamente OpenAPI `10.0.12`; outro template/SDK pode gerar uma versão diferente. Confira `.csproj` e `Directory.Packages.props`. |
| Restore por GitHub retorna 401/403 | Confira feed, token classic, `read:packages`, usuário dono do token e acesso ao pacote; a máscara do exemplo não autentica. |
| Troca para pacotes causa conflito EF/Pomelo | Confira o `.nuspec` da versão consumida; o artefato local `0.1.0` de EF Core exige major 10, incompatível com Pomelo 9 do template. |
| Pacotes restauram no host, mas não no build Docker | O template não fornece configuração/autenticação NuGet ao build; ambiente do container em execução não configura o restore da imagem. |
| Docker falha em `COPY` de `.csproj` | Execute primeiro a geração completa e preserve o contexto `../..`; o ZIP não contém os projetos. |
| Build interrompido por analisadores apenas no Docker | Confira se o `Directory.Build.props` do sample foi copiado, como no Dockerfile fornecido, antes do restore/publish. |
| Imagem nova, comportamento antigo ou DLL ausente | Verifique a publicação do host montada em `/app`; republique com `buildForDocker.bat` e reinicie a WebApi. |
| WebApi falha antes de atender HTTP | Leia os logs de migrations/conexão; confira MariaDB saudável, `.env`, secret e o alvo usado por `ServerVersion.AutoDetect`. |
| Mudança de senha no arquivo não funciona com volume antigo | Credenciais já inicializadas no MariaDB persistem no banco; editar o arquivo não altera automaticamente as contas existentes. |
| Dashboard parece parado em Pending | Clique em Refresh Data, confira Chaos, quantidade de tentativas e `next_attempt_at`; Pending pode representar espera programada. |
| Dashboard retorna erro logo no startup | Banco saudável não garante que as tabelas já foram criadas pela WebApi; aguarde a inicialização e veja os logs dos dois serviços. |
| k6 falha no segundo teste | Confira e-mails duplicados e prontidão da API; o wrapper não limpa usuários. |
| Grafana sem dados | Confira `/metrics`, o alvo no Prometheus, datasource, intervalo selecionado e o reset de histórico feito pelo wrapper k6. Nem toda consulta do dashboard importado necessariamente corresponde às séries disponíveis. |
| Debug local falha, mas container funciona | O perfil DEV não herda a conexão/SMTP do Compose; configure o ambiente do processo .NET no host. |

## Escopo da verificação deste guia

O conteúdo foi conferido por leitura dos scripts, do ZIP, da solução, do código atual da biblioteca e dos metadados de pacotes locais. O Compose do ZIP foi comparado ao anexo fornecido e analisado pelo parser do Docker Compose sem resolver caminhos, interpolar ambiente ou exigir os arquivos ainda não extraídos. Essa verificação confirma a estrutura declarada dos sete serviços; não substitui um teste de startup.

Na elaboração da documentação, não foram executados bootstrap, publish, migrations, carga k6 ou inicialização/recriação de containers. A publicação no GitHub foi informada pelo autor: não houve autenticação no feed, download de pacote remoto nem push para verificar a versão publicada. As verificações automatizadas da biblioteca e seus resultados históricos estão em [implementation-status.md](implementation-status.md); esse registro histórico não é um inventário atual de publicações. Este ambiente manual de integração complementa aquelas verificações.
