define([
    "dojo/_base/declare",
    "dojo/_base/lang",
    "dojo/dom-construct",
    "dojo/dom-style",
    "dojo/on",
    "dojo/request/xhr",
    "dojo/topic",
    "dojo/window",
    "dojo/i18n!cheapskate/nls/LanguageVersionCreator",
    "dijit/_WidgetBase",
    "dijit/_TemplatedMixin",
    "dijit/_WidgetsInTemplateMixin",
    "dijit/Dialog"
], function (
    declare,
    lang,
    domConstruct,
    domStyle,
    on,
    xhr,
    topic,
    win,
    i18n,
    _WidgetBase,
    _TemplatedMixin,
    _WidgetsInTemplateMixin,
    Dialog
) {
    return declare([Dialog], {
        title: i18n.dialogTitle,
        contentLink: null,
        style: "width: 550px; max-height: calc(100vh - 32px); overflow-y: auto;",
        autofocus: false,
        _format: function (template, value) {
            return template.replace("{0}", value);
        },

        postCreate: function () {
            this.inherited(arguments);
            this._buildInitialContent();
            this._loadOptions();
        },

        onShow: function () {
            this.inherited(arguments);
            this._centerInViewport();
        },

        _centerInViewport: function () {
            if (!this.domNode) {
                return;
            }

            var viewport = win.getBox();
            var height = this.domNode.offsetHeight || 0;
            var top = Math.max(16, (viewport.h - height) / 2);
            domStyle.set(this.domNode, "top", top + "px");
        },

        _buildInitialContent: function () {
            this._container = domConstruct.create("div", {
                style: "padding: 12px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; font-size: 13px; line-height: 1.5; color: #333;"
            });
            this.set("content", this._container);
            this._loadingDiv = domConstruct.create("div", {
                 innerHTML: "<p style='color: #666; text-align: center; padding: 20px 0;'>" + i18n.loadingLanguageOptions + "</p>"
            }, this._container);
        },

        _loadOptions: function () {
            if (!this.contentLink) {
                this._showError(i18n.noContentSelected);
                return;
            }

            xhr.get("/api/cheapskate/ai-translator/language-version-creator/options", {
                query: { contentLink: this.contentLink },
                handleAs: "json",
                headers: { "Accept": "application/json" }
            }).then(lang.hitch(this, function (data) {
                this._optionsData = data;
                this._renderForm(data);
            }), lang.hitch(this, function (err) {
                var message = i18n.failedToLoadOptions;
                if (err && err.response && err.response.data && err.response.data.message) {
                    message = err.response.data.message;
                }
                this._showError(message);
            }));
        },

        // The server returns contentLink as a serialized ContentReference object ({ id, workID, ... })
        // rather than a plain string; pull out the numeric id for display.
        _formatContentLink: function (contentLink) {
            if (contentLink == null) {
                return "";
            }
            if (typeof contentLink === "string" || typeof contentLink === "number") {
                return contentLink;
            }
            if (typeof contentLink.id !== "undefined") {
                return contentLink.workID ? (contentLink.id + "_" + contentLink.workID) : contentLink.id;
            }
            return String(contentLink);
        },

        _renderForm: function (data) {
            domConstruct.empty(this._container);

            var header = domConstruct.create("div", {
                style: "margin-bottom: 16px; border-bottom: 1px solid #e0e0e0; padding-bottom: 10px;"
            }, this._container);

            domConstruct.create("div", {
                 innerHTML: "<strong>" + i18n.content + "</strong> " + (data.contentName || i18n.unnamedContent) + " <span style='color: #888;'> (" + this._formatContentLink(data.contentLink) + ")</span>"
            }, header);

            domConstruct.create("div", {
                innerHTML: "<strong>" + i18n.masterLanguage + "</strong> <span style='background: #e8f4fd; color: #0070d2; padding: 2px 6px; border-radius: 4px; font-weight: 600;'>" + data.masterLanguage + "</span>",
                style: "margin-top: 4px;"
            }, header);

            var missingLanguages = (data.languages || []).filter(function (l) {
                return !l.existsOnContent && !l.isMasterLanguage;
            });

            if (missingLanguages.length === 0) {
                domConstruct.create("div", {
                    innerHTML: "<div style='background: #eef9e8; color: #2e7d32; border: 1px solid #c8e6c9; padding: 12px; border-radius: 4px; margin-bottom: 16px;'>" +
                        "<strong>" + i18n.allLanguagesExist + "</strong> " + i18n.noNewDrafts + "</div>"
                }, this._container);

                var closeFooter = domConstruct.create("div", {
                    style: "text-align: right; margin-top: 16px;"
                }, this._container);

                var closeBtn = domConstruct.create("button", {
                    innerHTML: i18n.close,
                    className: "epi-button-child",
                    style: "padding: 6px 16px; cursor: pointer; border-radius: 3px; border: 1px solid #ccc; background: #fff;"
                }, closeFooter);

                on(closeBtn, "click", lang.hitch(this, function () {
                    this.hide();
                }));
                return;
            }

            var form = domConstruct.create("div", { style: "margin-bottom: 16px;" }, this._container);

            domConstruct.create("div", {
                innerHTML: "<strong>" + i18n.selectTargetLanguages + "</strong>",
                style: "margin-bottom: 8px;"
            }, form);

            // Radio 1: All Languages
            var allOptionDiv = domConstruct.create("div", { style: "margin-bottom: 8px;" }, form);
            this._allRadio = domConstruct.create("input", {
                type: "radio",
                name: "targetLangChoice",
                id: "lang_all_" + this.id,
                checked: true,
                value: "all"
            }, allOptionDiv);
            domConstruct.create("label", {
                for: "lang_all_" + this.id,
                innerHTML: " <strong>" + i18n.allMissingLanguages + "</strong> (" + missingLanguages.map(function(l){ return l.languageId; }).join(", ") + ")",
                style: "margin-left: 6px; cursor: pointer;"
            }, allOptionDiv);

            // Radio 2: Specific Language
            var specificOptionDiv = domConstruct.create("div", { style: "margin-bottom: 8px; display: flex; align-items: center;" }, form);
            this._specificRadio = domConstruct.create("input", {
                type: "radio",
                name: "targetLangChoice",
                id: "lang_specific_" + this.id,
                value: "specific"
            }, specificOptionDiv);
            domConstruct.create("label", {
                for: "lang_specific_" + this.id,
                innerHTML: " <strong>" + i18n.specificLanguage + "</strong>",
                style: "margin-left: 6px; margin-right: 8px; cursor: pointer;"
            }, specificOptionDiv);

            this._langSelect = domConstruct.create("select", {
                style: "padding: 4px 8px; border-radius: 3px; border: 1px solid #ccc;"
            }, specificOptionDiv);

            missingLanguages.forEach(function (l) {
                domConstruct.create("option", {
                    value: l.languageId,
                    innerHTML: l.displayName + " (" + l.languageId + ")"
                }, this._langSelect);
            }, this);

            on(this._langSelect, "focus", lang.hitch(this, function () {
                this._specificRadio.checked = true;
            }));

            var linkedContentsOption = domConstruct.create("label", {
                style: "display: block; margin-top: 12px; cursor: pointer;"
            }, form);
            this._linkedContentsCheckbox = domConstruct.create("input", {
                type: "checkbox",
                checked: false,
                style: "margin-right: 6px;"
            }, linkedContentsOption);
            domConstruct.create("span", {
                innerHTML: i18n.includeLinkedContents
            }, linkedContentsOption);

            // Info Box
            domConstruct.create("div", {
                style: "background: #f8f9fa; border: 1px solid #e9ecef; border-left: 4px solid #0070d2; padding: 10px 12px; border-radius: 2px; font-size: 12px; color: #495057; margin-top: 12px;",
                innerHTML: "<ul style='margin: 0; padding-left: 18px;'>" +
                    "<li>" + i18n.draftInfo + "</li>" +
                    "<li>" + i18n.languageSpecificInfo + "</li>" +
                    "<li>" + i18n.inheritedInfo + "</li>" +
                    "<li>" + i18n.recursiveInfo + "</li>" +
                    "</ul>"
            }, this._container);

            if (data.aiTranslationConfigured) {
                var aiOption = domConstruct.create("label", {
                    style: "display: block; margin-top: 12px; cursor: pointer;"
                }, this._container);
                this._aiCheckbox = domConstruct.create("input", {
                    type: "checkbox",
                    checked: data.aiTranslationConfigured === true,
                    style: "margin-right: 6px;"
                }, aiOption);
                domConstruct.create("span", {
                    innerHTML: i18n.useAiTranslation
                }, aiOption);

                var toneOption = domConstruct.create("label", {
                    style: "display: block; margin-top: 8px;"
                }, this._container);
                domConstruct.create("span", {
                    innerHTML: i18n.translationTone + " "
                }, toneOption);
                this._toneSelect = domConstruct.create("select", {
                    style: "padding: 4px 8px; border-radius: 3px; border: 1px solid #ccc;"
                }, toneOption);
                [
                    ["Neutral", i18n.toneNeutral],
                    ["Formal", i18n.toneFormal],
                    ["Academic", i18n.toneAcademic],
                    ["Casual", i18n.toneCasual],
                    ["Business", i18n.toneBusiness],
                    ["Humorous", i18n.toneHumorous],
                    ["Sarcastic", i18n.toneSarcastic]
                ].forEach(function (tone) {
                    domConstruct.create("option", {
                        value: tone[0],
                        innerHTML: tone[1]
                    }, this._toneSelect);
                }, this);
            }

            this._statusMessageDiv = domConstruct.create("div", {
                style: "margin-top: 12px; display: none;"
            }, this._container);

            // Footer actions
            var footer = domConstruct.create("div", {
                style: "text-align: right; margin-top: 18px; border-top: 1px solid #e0e0e0; padding-top: 12px;"
            }, this._container);

            this._cancelBtn = domConstruct.create("button", {
                innerHTML: i18n.cancel,
                className: "epi-button-child",
                style: "padding: 6px 14px; cursor: pointer; border-radius: 3px; border: 1px solid #ccc; background: #fff; margin-right: 8px;"
            }, footer);

            this._submitBtn = domConstruct.create("button", {
                innerHTML: i18n.createDrafts,
                className: "epi-button-child epi-button-child--primary",
                style: "padding: 6px 16px; cursor: pointer; border-radius: 3px; border: 1px solid #0070d2; background: #0070d2; color: #fff; font-weight: 600;"
            }, footer);

            on(this._cancelBtn, "click", lang.hitch(this, function () {
                this.hide();
            }));

            on(this._submitBtn, "click", lang.hitch(this, this._onSubmit));
            this._centerInViewport();
        },

        _onSubmit: function () {
            var isAll = this._allRadio.checked;
            var targetLanguages = isAll ? [] : [this._langSelect.value];

            this._submitBtn.disabled = true;
            this._cancelBtn.disabled = true;
            this._submitBtn.innerHTML = i18n.creatingDrafts;
            this._submitBtn.style.opacity = "0.7";

            domStyle.set(this._statusMessageDiv, "display", "block");
            this._statusMessageDiv.innerHTML = "<div style='color: #0070d2; font-style: italic;'>" + i18n.processingDrafts + "</div>";

            var payload = {
                contentLink: this.contentLink,
                allLanguages: isAll,
                targetLanguages: targetLanguages,
                includeLinkedContents: !!this._linkedContentsCheckbox.checked,
                useAiTranslation: !!(this._aiCheckbox && this._aiCheckbox.checked),
                tone: this._toneSelect ? this._toneSelect.value : "Neutral"
            };

            xhr.post("/api/cheapskate/ai-translator/language-version-creator/create", {
                data: JSON.stringify(payload),
                handleAs: "json",
                headers: {
                    "Content-Type": "application/json",
                    "Accept": "application/json"
                }
            }).then(lang.hitch(this, function (result) {
                this._renderResult(result);
                this._pollTranslationJobs(result.translationJobIds || []);
            }), lang.hitch(this, function (err) {
                this._submitBtn.disabled = false;
                this._cancelBtn.disabled = false;
                this._submitBtn.innerHTML = i18n.createDrafts;
                this._submitBtn.style.opacity = "1";

                var message = i18n.creationError;
                if (err && err.response && err.response.data && err.response.data.message) {
                    message = err.response.data.message;
                }
                domStyle.set(this._statusMessageDiv, "display", "block");
                this._statusMessageDiv.innerHTML = "<div style='background: #fde8e8; color: #c62828; border: 1px solid #f8b4b4; padding: 10px; border-radius: 4px;'>" +
                    "<strong>" + i18n.error + "</strong> " + message + "</div>";
            }));
        },

        _pollTranslationJobs: function (jobIds) {
            if (!jobIds.length) {
                return;
            }
            var pending = jobIds.length;
            var poll = lang.hitch(this, function () {
                var requests = jobIds.map(function (jobId) {
                    return xhr.get("/api/cheapskate/ai-translator/language-version-creator/translation-status/" + jobId, {
                        handleAs: "json",
                        headers: { "Accept": "application/json" }
                    });
                });
                Promise.all(requests).then(lang.hitch(this, function (statuses) {
                    pending = statuses.filter(function (status) {
                        return status.state === 0 || status.state === 1 || status.state === "Queued" || status.state === "Running";
                    }).length;
                    if (this._statusMessageDiv) {
                        this._statusMessageDiv.innerHTML = pending ?
                            "<div style='color: #0070d2; font-style: italic;'>" + this._format(i18n.aiTranslationRunning, pending) + "</div>" :
                            "<div style='color: #2e7d32;'>" + i18n.aiTranslationCompleted + "</div>";
                    }
                    if (pending) {
                        setTimeout(poll, 1500);
                    }
                }));
            });
            poll();
        },

        _renderResult: function (result) {
            domConstruct.empty(this._container);

            var summaryHeader = domConstruct.create("div", {
                style: "margin-bottom: 16px; border-bottom: 1px solid #e0e0e0; padding-bottom: 10px;"
            }, this._container);

            domConstruct.create("div", {
                innerHTML: "<strong style='font-size: 14px; color: #2e7d32;'>" + i18n.creationComplete + "</strong>"
            }, summaryHeader);

            var statsGrid = domConstruct.create("div", {
                style: "display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; margin-bottom: 16px;"
            }, this._container);

            this._createStatCard(statsGrid, i18n.draftsCreated, result.totalCreated || 0, "#2e7d32", "#eef9e8");
            this._createStatCard(statsGrid, i18n.skipped, result.totalSkipped || 0, "#f57c00", "#fff3e0");
            this._createStatCard(statsGrid, i18n.failed, result.totalFailed || 0, "#c62828", "#fde8e8");

            if (result.items && result.items.length > 0) {
                var tableContainer = domConstruct.create("div", {
                    style: "max-height: 250px; overflow-y: auto; border: 1px solid #e0e0e0; border-radius: 3px; margin-bottom: 16px;"
                }, this._container);

                var table = domConstruct.create("table", {
                    style: "width: 100%; border-collapse: collapse; font-size: 12px;"
                }, tableContainer);

                var thead = domConstruct.create("thead", {
                    style: "background: #f5f5f5; position: sticky; top: 0;"
                }, table);

                var headerRow = domConstruct.create("tr", {}, thead);
                [i18n.name, i18n.type, i18n.language, i18n.status].forEach(function (h) {
                    domConstruct.create("th", {
                        innerHTML: h,
                        style: "text-align: left; padding: 6px 8px; border-bottom: 1px solid #ddd; font-weight: 600;"
                    }, headerRow);
                });

                var tbody = domConstruct.create("tbody", {}, table);
                result.items.forEach(function (item) {
                    var row = domConstruct.create("tr", {
                        style: "border-bottom: 1px solid #eee;"
                    }, tbody);

                    domConstruct.create("td", {
                        innerHTML: item.name || i18n.unnamed,
                        style: "padding: 6px 8px; max-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                    }, row);

                    domConstruct.create("td", {
                        innerHTML: item.contentType || "",
                        style: "padding: 6px 8px; color: #666;"
                    }, row);

                    domConstruct.create("td", {
                        innerHTML: "<span style='background: #eee; padding: 2px 5px; border-radius: 3px;'>" + item.language + "</span>",
                        style: "padding: 6px 8px;"
                    }, row);

                    var statusText = "";
                    var statusColor = "#333";
                    if (item.status === 0 || item.status === "CreatedDraft") {
                        statusText = i18n.createdDraft;
                        statusColor = "#2e7d32";
                    } else if (item.status === 1 || item.status === "SkippedAlreadyExists") {
                        statusText = i18n.skippedExists;
                        statusColor = "#888";
                    } else if (item.status === 2 || item.status === "SkippedDuplicateInRun") {
                        statusText = i18n.skippedDedup;
                        statusColor = "#888";
                    } else {
                        statusText = i18n.failedWithReason.replace("{0}", item.reason || i18n.error);
                        statusColor = "#c62828";
                    }

                    domConstruct.create("td", {
                        innerHTML: statusText,
                        style: "padding: 6px 8px; font-weight: 600; color: " + statusColor + ";"
                    }, row);
                });
            }

            var footer = domConstruct.create("div", {
                style: "text-align: right; margin-top: 16px; border-top: 1px solid #e0e0e0; padding-top: 12px;"
            }, this._container);

            var closeBtn = domConstruct.create("button", {
                innerHTML: i18n.closeRefresh,
                className: "epi-button-child epi-button-child--primary",
                style: "padding: 6px 16px; cursor: pointer; border-radius: 3px; border: 1px solid #0070d2; background: #0070d2; color: #fff; font-weight: 600;"
            }, footer);

            on(closeBtn, "click", lang.hitch(this, function () {
                // Notify CMS of content update so language branches and navigation reload
                topic.publish("/epi/cms/contentdata/updated", this.contentLink);
                this.hide();
            }));
        },

        _createStatCard: function (parent, title, value, textColor, bgColor) {
            var card = domConstruct.create("div", {
                style: "background: " + bgColor + "; border-radius: 4px; padding: 8px 12px; text-align: center;"
            }, parent);
            domConstruct.create("div", {
                innerHTML: String(value),
                style: "font-size: 20px; font-weight: 700; color: " + textColor + ";"
            }, card);
            domConstruct.create("div", {
                innerHTML: title,
                style: "font-size: 11px; color: #555; margin-top: 2px;"
            }, card);
        },

        _showError: function (message) {
            domConstruct.empty(this._container);
            domConstruct.create("div", {
                style: "background: #fde8e8; color: #c62828; border: 1px solid #f8b4b4; padding: 12px; border-radius: 4px; margin-bottom: 16px;",
                innerHTML: "<strong>" + i18n.error + "</strong> " + message
            }, this._container);

            var footer = domConstruct.create("div", {
                style: "text-align: right;"
            }, this._container);

            var closeBtn = domConstruct.create("button", {
                innerHTML: i18n.close,
                className: "epi-button-child",
                style: "padding: 6px 14px; cursor: pointer; border-radius: 3px; border: 1px solid #ccc; background: #fff;"
            }, footer);

            on(closeBtn, "click", lang.hitch(this, function () {
                this.hide();
            }));
        }
    });
});
