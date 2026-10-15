define([
    "dojo/_base/declare",
    "dojo/_base/lang",
    "dojo/request/xhr",
    "dojo/topic",
    "dojo/i18n!cheapskate/nls/LanguageVersionCreator",
    "epi/dependency",
    "epi/shell/command/_Command",
    "cheapskate/widgets/LanguageVersionCreatorDialog"
], function (
    declare,
    lang,
    xhr,
    topic,
    i18n,
    dependency,
    _Command,
    LanguageVersionCreatorDialog
) {
    var localizabilityCache = {};

    return declare([_Command], {
        name: "createLanguageVersions",
        label: i18n.commandLabel,
        tooltip: i18n.commandTooltip,
        iconClass: "cheapskate-skate-icon",
        category: "menu",
        canExecute: false,
        isAvailable: false,

        constructor: function () {
            this.inherited(arguments);
            this._contextSubscriptions = [
                topic.subscribe("/epi/shell/context/current", lang.hitch(this, this._onContextChange)),
                topic.subscribe("/epi/shell/context/changed", lang.hitch(this, this._onContextChange))
            ];
            this._updateState();
        },

        destroy: function () {
            if (this._contextSubscriptions) {
                for (var i = 0; i < this._contextSubscriptions.length; i++) {
                    this._contextSubscriptions[i].remove();
                }
                this._contextSubscriptions = null;
            }
            this.inherited(arguments);
        },

        _onModelChange: function () {
            this.inherited(arguments);
            this._updateState();
        },

        _onContextChange: function (newContext) {
            this._context = newContext;
            this._updateState();
        },

        _updateState: function () {
            var contentLink = this._getContentLink();
            var cacheKey = contentLink == null ? null : String(contentLink);

            this.set("canExecute", false);
            this.set("isAvailable", false);
            if (!cacheKey) {
                return;
            }

            var cached = localizabilityCache[cacheKey];
            if (cached && typeof cached.then === "function") {
                return;
            }
            if (Object.prototype.hasOwnProperty.call(localizabilityCache, cacheKey)) {
                this.set("canExecute", cached);
                this.set("isAvailable", cached);
                return;
            }

            localizabilityCache[cacheKey] = xhr.get("/api/cheapskate/ai-translator/language-version-creator/is-localizable", {
                query: { contentLink: contentLink },
                handleAs: "json",
                headers: { "Accept": "application/json" }
            }).then(lang.hitch(this, function () {
                localizabilityCache[cacheKey] = true;
                if (String(this._getContentLink()) === cacheKey) {
                    this.set("canExecute", true);
                    this.set("isAvailable", true);
                }
            }), function (error) {
                var status = error && error.response && error.response.status;
                if (status === 400) {
                    localizabilityCache[cacheKey] = false;
                } else {
                    delete localizabilityCache[cacheKey];
                }
            });
        },

        _getContentLink: function () {
            if (this.model) {
                if (typeof this.model === "string") {
                    return this.model;
                }
                if (this.model.contentLink) {
                    return this.model.contentLink;
                }
                if (this.model.contentData && this.model.contentData.contentLink) {
                    return this.model.contentData.contentLink;
                }
                if (this.model.currentContent && this.model.currentContent.contentLink) {
                    return this.model.currentContent.contentLink;
                }
                if (this.model.get && typeof this.model.get === "function") {
                    var link = this.model.get("contentLink") || this.model.get("id");
                    if (link) {
                        return link;
                    }
                }
                if (this.model.id) {
                    return this.model.id;
                }
            }

            var context = this._context;
            if (!context) {
                try {
                    var contextService = dependency.resolve("epi.shell.ContextService");
                    context = contextService && contextService.currentContext;
                } catch (e) {
                    context = null;
                }
            }
            return context && context.id ? context.id : null;
        },

        _execute: function () {
            var contentLink = this._getContentLink();
            if (contentLink) {
                new LanguageVersionCreatorDialog({ contentLink: contentLink }).show();
            }
        }
    });
});
